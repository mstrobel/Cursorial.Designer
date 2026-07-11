package dev.cursorial.designer.previewer

import com.intellij.execution.ExecutionException
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.process.OSProcessHandler
import com.intellij.execution.process.ProcessEvent
import com.intellij.execution.process.ProcessListener
import com.intellij.execution.process.ProcessOutputType
import com.intellij.openapi.Disposable
import com.intellij.openapi.diagnostic.logger
import com.intellij.util.concurrency.AppExecutorUtil
import com.intellij.util.io.BaseOutputReader
import dev.cursorial.designer.protocol.LineProtocolCodec
import dev.cursorial.designer.protocol.MalformedEventException
import dev.cursorial.designer.protocol.PreviewerCommand
import dev.cursorial.designer.protocol.PreviewerEvent
import dev.cursorial.designer.protocol.ShutdownCommand
import java.io.IOException
import java.nio.charset.StandardCharsets
import java.nio.file.Path
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Owns one out-of-process preview host: `dotnet <hostDll>`.
 *
 * The host speaks newline-delimited JSON: commands are written to its stdin, events are
 * read from its stdout, and stderr is surfaced as host log output. If the process dies
 * unexpectedly it is restarted with a bounded backoff; subscribers get [Listener.onStarted]
 * again and are expected to re-send `initialize`/`loadXaml`.
 *
 * Dispose this object (it is a [Disposable]) to shut the host down; register it with the
 * owning editor so the process dies with the editor.
 */
class PreviewHostProcess(
    private val hostDllPath: Path,
    private val workingDirectory: Path? = null,
    private val dotnetExecutable: String = "dotnet",
) : Disposable {

    interface Listener {
        /** The host process has (re)started. Send `initialize` and re-load state from here. */
        fun onStarted() {}

        /** A protocol event arrived on the host's stdout. Called on a background thread. */
        fun onEvent(event: PreviewerEvent) {}

        /** A line of host stderr output (host logs). Called on a background thread. */
        fun onStderrLine(line: String) {}

        /**
         * The process terminated. [willRestart] is false when this was a shutdown or the restart
         * budget ran out; [expected] is true for deliberate endings (user/watcher restart,
         * shutdown, dispose) — only an unexpected death is a crash worth alarming about.
         */
        fun onTerminated(exitCode: Int, willRestart: Boolean, expected: Boolean = false) {}
    }

    companion object {
        private val logger = logger<PreviewHostProcess>()

        private const val MAX_RESTART_ATTEMPTS = 3
        private const val RESTART_DELAY_MS = 1_000L

        /** A run longer than this is considered healthy and resets the restart budget. */
        private const val HEALTHY_RUN_MS = 30_000L
    }

    private val listeners = CopyOnWriteArrayList<Listener>()
    private val disposed = AtomicBoolean(false)
    private val shutdownRequested = AtomicBoolean(false)
    private val restartPending = AtomicBoolean(false)

    private val stateLock = Any()
    private var processHandler: OSProcessHandler? = null
    private var restartAttempts = 0
    private var lastStartTimeMs = 0L

    private val stdoutBuffer = StringBuilder()
    private val stderrBuffer = StringBuilder()

    fun addListener(listener: Listener) {
        listeners.add(listener)
    }

    fun removeListener(listener: Listener) {
        listeners.remove(listener)
    }

    /** Starts the host process. No-op if already running or disposed. */
    fun start() {
        if (disposed.get()) return
        synchronized(stateLock) {
            if (processHandler?.isProcessTerminated == false) return
            startLocked()
        }
    }

    /**
     * Writes one command line to the host's stdin.
     * Returns false when the process is not running (the command is dropped, not queued).
     */
    fun sendCommand(command: PreviewerCommand): Boolean {
        val handler = synchronized(stateLock) { processHandler } ?: return false
        if (handler.isProcessTerminated || handler.isProcessTerminating) return false
        val input = handler.processInput ?: return false

        val line = LineProtocolCodec.encodeCommand(command)
        return try {
            synchronized(input) {
                input.write(line.toByteArray(StandardCharsets.UTF_8))
                input.write('\n'.code)
                input.flush()
            }
            true
        } catch (e: IOException) {
            logger.warn("Failed to write command to preview host: ${e.message}")
            false
        }
    }

    val isRunning: Boolean
        get() = synchronized(stateLock) { processHandler?.isProcessTerminated == false }

    /**
     * Stops the host (graceful shutdown request, then destroy) and starts a fresh one as soon as
     * the old process reports terminated — used when the session must be re-initialized (e.g. a
     * capability-profile change) or the user asks for a restart. Subscribers get [Listener.onStarted]
     * and re-send `initialize`/`loadXaml`, exactly like the crash-restart path.
     */
    fun restart() {
        if (disposed.get()) return

        val handler = synchronized(stateLock) { processHandler }
        if (handler == null || handler.isProcessTerminated) {
            start()
            return
        }

        restartPending.set(true)
        sendCommand(ShutdownCommand())
        handler.destroyProcess()
    }

    override fun dispose() {
        if (!disposed.compareAndSet(false, true)) return
        shutdownRequested.set(true)

        val handler = synchronized(stateLock) { processHandler }
        if (handler != null && !handler.isProcessTerminated) {
            // Ask nicely first; the host flushes and exits on "shutdown".
            sendCommand(ShutdownCommand())
            handler.destroyProcess()
        }
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private fun startLocked() {
        val commandLine = GeneralCommandLine(dotnetExecutable, hostDllPath.toAbsolutePath().toString())
            .withCharset(StandardCharsets.UTF_8)
            .apply {
                workingDirectory?.let { withWorkDirectory(it.toFile()) }
            }

        logger.info("Starting preview host: ${commandLine.commandLineString}")

        val handler = try {
            object : OSProcessHandler(commandLine) {
                // The host emits output only when frames/events happen; use the non-blocking
                // reader mode recommended for such processes (same as AvaloniaRider).
                override fun readerOptions(): BaseOutputReader.Options =
                    BaseOutputReader.Options.forMostlySilentProcess()
            }
        } catch (e: ExecutionException) {
            logger.warn("Failed to start preview host process", e)
            fireTerminated(exitCode = -1, willRestart = false, expected = true)
            return
        }

        handler.addProcessListener(object : ProcessListener {
            override fun onTextAvailable(event: ProcessEvent, outputType: com.intellij.openapi.util.Key<*>) {
                when {
                    ProcessOutputType.isStdout(outputType) -> consume(stdoutBuffer, event.text, ::handleStdoutLine)
                    ProcessOutputType.isStderr(outputType) -> consume(stderrBuffer, event.text, ::handleStderrLine)
                }
            }

            override fun processTerminated(event: ProcessEvent) {
                handleTermination(event.exitCode)
            }
        })

        processHandler = handler
        lastStartTimeMs = System.currentTimeMillis()
        stdoutBuffer.setLength(0)
        stderrBuffer.setLength(0)
        handler.startNotify()

        for (listener in listeners) listener.onStarted()
    }

    /** Appends [text] to [buffer] and dispatches every complete line to [handleLine]. */
    private fun consume(buffer: StringBuilder, text: String, handleLine: (String) -> Unit) {
        synchronized(buffer) {
            buffer.append(text)
            while (true) {
                val newline = buffer.indexOf("\n")
                if (newline < 0) break
                val line = buffer.substring(0, newline).trimEnd('\r')
                buffer.delete(0, newline + 1)
                if (line.isNotEmpty()) handleLine(line)
            }
        }
    }

    private fun handleStdoutLine(line: String) {
        val event = try {
            LineProtocolCodec.decodeEvent(line)
        } catch (e: MalformedEventException) {
            logger.warn("Dropping malformed event from preview host: ${e.message}")
            return
        } ?: return

        for (listener in listeners) {
            try {
                listener.onEvent(event)
            } catch (t: Throwable) {
                logger.error("Preview host event listener failed", t)
            }
        }
    }

    private fun handleStderrLine(line: String) {
        logger.info("PreviewHost stderr: $line")
        for (listener in listeners) listener.onStderrLine(line)
    }

    private fun handleTermination(exitCode: Int) {
        val restart: Boolean
        val expected: Boolean
        synchronized(stateLock) {
            processHandler = null

            val ranHealthy = System.currentTimeMillis() - lastStartTimeMs > HEALTHY_RUN_MS
            if (ranHealthy) restartAttempts = 0

            val explicitRestart = restartPending.getAndSet(false)
            expected = explicitRestart || shutdownRequested.get() || disposed.get()
            restart = !disposed.get() &&
                !shutdownRequested.get() &&
                (explicitRestart || (exitCode != 0 && restartAttempts < MAX_RESTART_ATTEMPTS))
            if (restart && !explicitRestart) restartAttempts++
        }

        logger.info("Preview host terminated with exit code $exitCode (restart=$restart, expected=$expected)")
        fireTerminated(exitCode, restart, expected)

        if (restart) {
            AppExecutorUtil.getAppScheduledExecutorService().schedule(
                {
                    if (!disposed.get()) start()
                },
                RESTART_DELAY_MS,
                TimeUnit.MILLISECONDS,
            )
        }
    }

    private fun fireTerminated(exitCode: Int, willRestart: Boolean, expected: Boolean) {
        for (listener in listeners) {
            try {
                listener.onTerminated(exitCode, willRestart, expected)
            } catch (t: Throwable) {
                logger.error("Preview host termination listener failed", t)
            }
        }
    }
}
