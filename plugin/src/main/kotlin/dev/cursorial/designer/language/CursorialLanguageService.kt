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

    /** The host binary's timestamp at spawn — a rebuilt host must serve the NEW bits. */
    @Volatile
    private var hostDllStamp = 0L

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

        override fun onTerminated(exitCode: Int, willRestart: Boolean, expected: Boolean) {
            // Fail everything in flight; callers degrade gracefully (no squiggles, no items).
            val inFlight = pending.values.toList()
            pending.clear()
            for (future in inFlight) future.completeExceptionally(TimeoutException("language service terminated"))
        }
    }

    /** Live diagnostics for a document snapshot; null when the service is unavailable or slow. */
    fun analyze(xaml: String, sourceUri: String?, contextFile: VirtualFile?, classify: Boolean = false, timeoutMs: Long = 5_000): DiagnosticsEvent? {
        val id = requestIds.incrementAndGet()
        return request(id, timeoutMs, contextFile) { assemblies ->
            AnalyzeCommand(id, xaml, sourceUri, assemblies, if (classify) true else null)
        } as? DiagnosticsEvent
    }

    /** Symbol info (signature/docs/value) at a 1-based (line, column); null when unavailable or nothing there. */
    fun hover(xaml: String, line: Int, column: Int, contextFile: VirtualFile?, timeoutMs: Long = 1_500): HoverInfoEvent? {
        val id = requestIds.incrementAndGet()
        return request(id, timeoutMs, contextFile) { assemblies ->
            HoverCommand(id, xaml, line, column, assemblies, contextFile?.path)
        } as? HoverInfoEvent
    }

    /** Source location of the symbol at a 1-based (line, column); null when unavailable. */
    fun definition(xaml: String, line: Int, column: Int, contextFile: VirtualFile?, timeoutMs: Long = 1_500): DefinitionEvent? {
        val id = requestIds.incrementAndGet()
        return request(id, timeoutMs, contextFile) { assemblies ->
            DefinitionCommand(id, xaml, line, column, assemblies, contextFile?.path)
        } as? DefinitionEvent
    }

    /** Completion items at a 1-based (line, column); null when unavailable or slow. */
    fun complete(xaml: String, line: Int, column: Int, contextFile: VirtualFile?, timeoutMs: Long = 2_000): CompletionsEvent? {
        val id = requestIds.incrementAndGet()
        return request(id, timeoutMs, contextFile) { assemblies ->
            CompleteCommand(id, xaml, line, column, assemblies)
        } as? CompletionsEvent
    }

    private fun assembliesFor(file: VirtualFile?): List<String> =
        file?.let { UserAssemblyLocator.locate(it).assemblies } ?: emptyList()

    private fun request(id: Int, timeoutMs: Long, contextFile: VirtualFile?, command: (List<String>) -> dev.cursorial.designer.protocol.PreviewerCommand): PreviewerEvent? {
        val assemblies = assembliesFor(contextFile)
        val host = ensureProcess(contextFile, assemblies) ?: return null
        val future = CompletableFuture<PreviewerEvent>()
        pending[id] = future
        if (!host.sendCommand(command(assemblies))) {
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

    /**
     * The registered user assemblies' timestamps at load. Guarded by the [ensureProcess] monitor.
     * A rebuilt USER assembly is invisible to a running host — the CLR never reloads an
     * already-loaded assembly at the same path, so new/removed view-model members would be
     * missing from completion until the process dies (the preview side restarts on its dll
     * watch for the same reason).
     */
    private val userAssemblyStamps = HashMap<String, Long>()

    @Synchronized
    private fun ensureProcess(contextFile: VirtualFile?, userAssemblies: List<String>): PreviewHostProcess? {
        val hostDll = CursorialDesignerSettings.getInstance(project).previewHostDllPath(contextFile)
        if (hostDll == null) {
            logger.info("Cursorial language service unavailable: PreviewHost dll not found")
            return process?.takeIf { it.isRunning }
        }

        val stamp = hostDll.toFile().lastModified()
        process?.takeIf { it.isRunning }?.let { existing ->
            if (stamp != hostDllStamp) {
                // The host binary was rebuilt: a language service serving stale code is the classic
                // "the feature exists but the IDE disagrees" trap. Restart onto the new bits; the
                // request that triggered this degrades gracefully and the next one is served fresh.
                logger.info("Preview host binary changed; restarting language service")
                hostDllStamp = stamp
                existing.restart()
            } else if (recordUserAssemblyStamps(userAssemblies)) {
                // A user assembly was rebuilt at the same path. If the build is still writing, the
                // next request sees another stamp change and restarts again — eventually consistent.
                logger.info("User assembly changed; restarting language service")
                existing.restart()
            }
            return existing
        }

        hostDllStamp = stamp
        recordUserAssemblyStamps(userAssemblies)
        val fresh = process ?: PreviewHostProcess(hostDll).also {
            it.addListener(listener)
            Disposer.register(this, it)
            process = it
        }
        fresh.start()
        return fresh
    }

    /** Records the assemblies' current stamps; true when a PREVIOUSLY SEEN assembly changed. */
    private fun recordUserAssemblyStamps(assemblies: List<String>): Boolean {
        var changed = false
        for (path in assemblies) {
            val stamp = java.io.File(path).lastModified()
            val previous = userAssemblyStamps.put(path, stamp)
            if (previous != null && previous != stamp) changed = true
        }
        return changed
    }

    override fun dispose() {
        // The process is disposed through Disposer (registered in ensureProcess).
    }
}
