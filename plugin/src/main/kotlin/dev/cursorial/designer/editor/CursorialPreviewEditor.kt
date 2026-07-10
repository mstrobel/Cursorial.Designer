package dev.cursorial.designer.editor

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ReadAction
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.fileEditor.FileEditor
import com.intellij.openapi.fileEditor.FileEditorState
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.util.UserDataHolderBase
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.ui.JBColor
import com.intellij.ui.components.JBLabel
import com.intellij.util.Alarm
import dev.cursorial.designer.protocol.AdvanceTimeCommand
import dev.cursorial.designer.protocol.DiagnosticsEvent
import dev.cursorial.designer.protocol.ErrorEvent
import dev.cursorial.designer.protocol.FrameEvent
import dev.cursorial.designer.protocol.HitTestCommand
import dev.cursorial.designer.protocol.HitTestResultEvent
import dev.cursorial.designer.protocol.InitializeCommand
import dev.cursorial.designer.protocol.KeyCommand
import dev.cursorial.designer.protocol.LoadXamlCommand
import dev.cursorial.designer.protocol.LogEvent
import dev.cursorial.designer.protocol.PointerCommand
import dev.cursorial.designer.protocol.PreviewerEvent
import dev.cursorial.designer.protocol.PropertiesEvent
import dev.cursorial.designer.protocol.ReadyEvent
import dev.cursorial.designer.protocol.ResizeCommand
import dev.cursorial.designer.protocol.ThemeBase
import dev.cursorial.designer.protocol.UnknownEvent
import dev.cursorial.designer.previewer.PreviewHostProcess
import dev.cursorial.designer.settings.CursorialDesignerSettings
import java.awt.BorderLayout
import java.beans.PropertyChangeListener
import java.util.concurrent.atomic.AtomicInteger
import javax.swing.JComponent
import javax.swing.JPanel
import javax.swing.SwingConstants

/**
 * The preview half of the Cursorial split editor: hosts the [CellGridPanel] and drives an
 * out-of-process [PreviewHostProcess] that renders the XAML headlessly.
 */
class CursorialPreviewEditor(
    private val project: Project,
    private val file: VirtualFile,
) : UserDataHolderBase(), FileEditor {

    companion object {
        private val logger = logger<CursorialPreviewEditor>()
        private const val RELOAD_DEBOUNCE_MS = 300
    }

    private val gridPanel = CellGridPanel()
    private val statusLabel = JBLabel("", SwingConstants.LEADING)
    private val rootPanel: JPanel = JPanel(BorderLayout()).apply {
        add(gridPanel, BorderLayout.CENTER)
        add(statusLabel, BorderLayout.SOUTH)
    }

    private val reloadAlarm = Alarm(Alarm.ThreadToUse.SWING_THREAD, this)
    private val requestIds = AtomicInteger()

    @Volatile
    private var pendingHitTestId: Int = -1

    private val document: Document? =
        ReadAction.compute<Document?, RuntimeException> {
            FileDocumentManager.getInstance().getDocument(file)
        }

    private var hostProcess: PreviewHostProcess? = null

    private val hostListener = object : PreviewHostProcess.Listener {
        override fun onEvent(event: PreviewerEvent) {
            when (event) {
                is ReadyEvent -> onHostReady(event)
                is FrameEvent -> onEdt { gridPanel.render(event) }
                is DiagnosticsEvent -> onEdt { showDiagnostics(event) }
                is HitTestResultEvent -> onEdt { showHitTestResult(event) }
                is PropertiesEvent -> logger.info("properties(replyTo=${event.replyTo}): ${event.items.size} items")
                is ErrorEvent -> onEdt { statusLabel.text = "Previewer error: ${event.message}" }
                is LogEvent -> logger.info("PreviewHost [${event.level}]: ${event.message}")
                is UnknownEvent -> logger.warn("Unknown event type \"${event.type}\" from preview host")
            }
        }

        override fun onTerminated(exitCode: Int, willRestart: Boolean) {
            onEdt {
                statusLabel.text =
                    if (willRestart) "Previewer crashed (exit code $exitCode); restarting…"
                    else "Previewer terminated (exit code $exitCode)"
            }
        }

        override fun onStderrLine(line: String) {
            if (logger.isDebugEnabled) logger.debug("PreviewHost stderr: $line")
        }
    }

    init {
        val hostDll = CursorialDesignerSettings.getInstance(project).previewHostDllPath(file)
        if (hostDll == null) {
            statusLabel.text =
                "Cursorial PreviewHost not found; set ${CursorialDesignerSettings.ENV_PREVIEW_HOST_DLL} " +
                    "or build it to ${CursorialDesignerSettings.DEFAULT_HOST_RELATIVE_PATH}"
        } else {
            val process = PreviewHostProcess(hostDll)
            Disposer.register(this, process)
            process.addListener(hostListener)
            hostProcess = process
            process.start()
        }

        gridPanel.resizeListener = { columns, rows ->
            hostProcess?.sendCommand(ResizeCommand(columns, rows))
        }
        gridPanel.pointerListener = { kind, column, row, button ->
            hostProcess?.sendCommand(PointerCommand(kind, column, row, button))
        }
        gridPanel.hitTestListener = { column, row ->
            val id = requestIds.incrementAndGet()
            pendingHitTestId = id
            hostProcess?.sendCommand(HitTestCommand(id, column, row))
        }
        gridPanel.keyListener = { key, modifiers ->
            hostProcess?.sendCommand(KeyCommand(key, modifiers))
        }

        document?.addDocumentListener(
            object : DocumentListener {
                override fun documentChanged(event: DocumentEvent) {
                    reloadAlarm.cancelAllRequests()
                    reloadAlarm.addRequest({ sendLoadXaml() }, RELOAD_DEBOUNCE_MS)
                }
            },
            this,
        )
    }

    private fun onHostReady(event: ReadyEvent) {
        logger.info("Preview host ready: protocol=${event.protocolVersion}, pid=${event.pid}")
        val process = hostProcess ?: return

        val (columns, rows) = gridPanel.gridSize()
        // TODO(verify): JBColor.isBright() as the light/dark signal; consider LafManager listener
        //  to send setTheme when the IDE theme changes.
        val themeBase = if (JBColor.isBright()) ThemeBase.LIGHT else ThemeBase.DARK
        process.sendCommand(InitializeCommand(columns = columns, rows = rows, themeBase = themeBase))
        sendLoadXaml()
        // Nudge the host to produce a first frame even for purely animation-driven UIs.
        process.sendCommand(AdvanceTimeCommand(0))
    }

    private fun sendLoadXaml() {
        val process = hostProcess ?: return
        val document = document ?: return
        val xaml = ReadAction.compute<String, RuntimeException> { document.text }
        process.sendCommand(
            LoadXamlCommand(
                xaml = xaml,
                sourceUri = file.url,
                // TODO: feed user-project assemblies once the host can load custom controls.
                assemblies = emptyList(),
            ),
        )
    }

    private fun showDiagnostics(event: DiagnosticsEvent) {
        // TODO: surface as editor annotations/inspections instead of a status line.
        val errors = event.items.filter { it.severity == "error" }
        statusLabel.text = when {
            event.items.isEmpty() -> ""
            errors.isEmpty() -> "${event.items.size} diagnostic(s)"
            else -> {
                val first = errors.first()
                "${errors.size} error(s) — ${first.code ?: ""} ${first.message} (${first.line}:${first.column})"
            }
        }
    }

    private fun showHitTestResult(event: HitTestResultEvent) {
        if (event.replyTo != pendingHitTestId) return
        val topElement = event.elements.firstOrNull()
        gridPanel.showSelection(topElement?.bounds)
        statusLabel.text = topElement?.let {
            "${it.elementType ?: "element"} ${it.name ?: "#${it.elementId}"}"
        } ?: ""
    }

    private fun onEdt(action: () -> Unit) {
        val application = ApplicationManager.getApplication()
        if (application.isDispatchThread) action() else application.invokeLater(action)
    }

    // ------------------------------------------------------------------
    // FileEditor implementation
    // ------------------------------------------------------------------

    override fun getComponent(): JComponent = rootPanel

    override fun getPreferredFocusedComponent(): JComponent = gridPanel

    override fun getName(): String = "Cursorial Preview"

    override fun getFile(): VirtualFile = file

    override fun setState(state: FileEditorState) {}

    override fun isModified(): Boolean = false

    override fun isValid(): Boolean = file.isValid

    override fun addPropertyChangeListener(listener: PropertyChangeListener) {}

    override fun removePropertyChangeListener(listener: PropertyChangeListener) {}

    override fun dispose() {
        // hostProcess is disposed through Disposer (registered in init).
    }
}
