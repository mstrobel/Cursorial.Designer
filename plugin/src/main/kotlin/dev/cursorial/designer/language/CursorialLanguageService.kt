package dev.cursorial.designer.language

import com.intellij.openapi.Disposable
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.service
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.vfs.VirtualFile
import dev.cursorial.designer.previewer.PreviewHostProcess
import dev.cursorial.designer.previewer.UserAssemblyLocator
import dev.cursorial.designer.protocol.AnalyzeCommand
import dev.cursorial.designer.protocol.CompleteCommand
import dev.cursorial.designer.protocol.CompletionsEvent
import dev.cursorial.designer.protocol.DefinitionCommand
import dev.cursorial.designer.protocol.DefinitionEvent
import dev.cursorial.designer.protocol.DiagnosticsEvent
import dev.cursorial.designer.protocol.ErrorEvent
import dev.cursorial.designer.protocol.HoverCommand
import dev.cursorial.designer.protocol.HoverInfoEvent
import dev.cursorial.designer.protocol.PreviewerEvent
import com.intellij.openapi.progress.ProgressManager
import dev.cursorial.designer.settings.CursorialDesignerSettings
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import java.util.concurrent.atomic.AtomicInteger

/**
 * The project's language-service backend: one preview-host process that never initializes a
 * preview session — it exists solely for the editor-service commands (`analyze`, `complete`),
 * which are valid before `initialize`. Shared by the annotator and the completion contributor;
 * independent of any open preview.
 */
@Service(Service.Level.PROJECT)
class CursorialLanguageService(private val project: Project) : Disposable {

    companion object {
        private val logger = logger<CursorialLanguageService>()

        fun getInstance(project: Project): CursorialLanguageService = project.service()
    }

    private val requestIds = AtomicInteger()
    private val pending = ConcurrentHashMap<Int, CompletableFuture<PreviewerEvent>>()

    @Volatile
    private var process: PreviewHostProcess? = null

    private val listener = object : PreviewHostProcess.Listener {
        override fun onEvent(event: PreviewerEvent) {
            val replyTo = when (event) {
                is DiagnosticsEvent -> event.replyTo
                is CompletionsEvent -> event.replyTo
                is HoverInfoEvent -> event.replyTo
                is DefinitionEvent -> event.replyTo
                is ErrorEvent -> event.replyTo
                else -> null
            } ?: return
            pending.remove(replyTo)?.complete(event)
        }

        override fun onTerminated(exitCode: Int, willRestart: Boolean) {
            // Fail everything in flight; callers degrade gracefully (no squiggles, no items).
            val inFlight = pending.values.toList()
            pending.clear()
            for (future in inFlight) future.completeExceptionally(TimeoutException("language service terminated"))
        }
    }

    /** Live diagnostics for a document snapshot; null when the service is unavailable or slow. */
    fun analyze(xaml: String, sourceUri: String?, contextFile: VirtualFile?, classify: Boolean = false, timeoutMs: Long = 5_000): DiagnosticsEvent? {
        val id = requestIds.incrementAndGet()
        return request(id, timeoutMs, contextFile) {
            AnalyzeCommand(id, xaml, sourceUri, assembliesFor(contextFile), if (classify) true else null)
        } as? DiagnosticsEvent
    }

    /** Symbol info (signature/docs/value) at a 1-based (line, column); null when unavailable or nothing there. */
    fun hover(xaml: String, line: Int, column: Int, contextFile: VirtualFile?, timeoutMs: Long = 1_500): HoverInfoEvent? {
        val id = requestIds.incrementAndGet()
        return request(id, timeoutMs, contextFile) {
            HoverCommand(id, xaml, line, column, assembliesFor(contextFile), contextFile?.path)
        } as? HoverInfoEvent
    }

    /** Source location of the symbol at a 1-based (line, column); null when unavailable. */
    fun definition(xaml: String, line: Int, column: Int, contextFile: VirtualFile?, timeoutMs: Long = 1_500): DefinitionEvent? {
        val id = requestIds.incrementAndGet()
        return request(id, timeoutMs, contextFile) {
            DefinitionCommand(id, xaml, line, column, assembliesFor(contextFile), contextFile?.path)
        } as? DefinitionEvent
    }

    /** Completion items at a 1-based (line, column); null when unavailable or slow. */
    fun complete(xaml: String, line: Int, column: Int, contextFile: VirtualFile?, timeoutMs: Long = 2_000): CompletionsEvent? {
        val id = requestIds.incrementAndGet()
        return request(id, timeoutMs, contextFile) {
            CompleteCommand(id, xaml, line, column, assembliesFor(contextFile))
        } as? CompletionsEvent
    }

    private fun assembliesFor(file: VirtualFile?): List<String> =
        file?.let { UserAssemblyLocator.locate(it).assemblies } ?: emptyList()

    private fun request(id: Int, timeoutMs: Long, contextFile: VirtualFile?, command: () -> dev.cursorial.designer.protocol.PreviewerCommand): PreviewerEvent? {
        val host = ensureProcess(contextFile) ?: return null
        val future = CompletableFuture<PreviewerEvent>()
        pending[id] = future
        if (!host.sendCommand(command())) {
            pending.remove(id)
            return null
        }

        // Poll in short slices instead of one hard get(): completion calls this inside a
        // cancellable read action, and a blocked read action stalls every queued write action —
        // i.e. typing freezes until the host answers or the timeout lapses. checkCanceled() lets
        // the platform abort us the moment a write action needs the lock (a no-op when no
        // progress indicator is active, so other callers lose nothing).
        val deadline = System.currentTimeMillis() + timeoutMs
        try {
            while (true) {
                ProgressManager.checkCanceled()
                try {
                    return future.get(25, TimeUnit.MILLISECONDS)
                } catch (_: TimeoutException) {
                    if (System.currentTimeMillis() >= deadline) return null
                } catch (_: InterruptedException) {
                    Thread.currentThread().interrupt()
                    return null
                } catch (_: ExecutionException) {
                    return null
                }
            }
        } finally {
            pending.remove(id)
        }
    }

    @Synchronized
    private fun ensureProcess(contextFile: VirtualFile?): PreviewHostProcess? {
        process?.takeIf { it.isRunning }?.let { return it }

        val hostDll = CursorialDesignerSettings.getInstance(project).previewHostDllPath(contextFile)
        if (hostDll == null) {
            logger.info("Cursorial language service unavailable: PreviewHost dll not found")
            return null
        }

        val fresh = process ?: PreviewHostProcess(hostDll).also {
            it.addListener(listener)
            Disposer.register(this, it)
            process = it
        }
        fresh.start()
        return fresh
    }

    override fun dispose() {
        // The process is disposed through Disposer (registered in ensureProcess).
    }
}
