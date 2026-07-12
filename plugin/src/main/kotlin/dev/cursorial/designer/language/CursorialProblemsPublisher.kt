package dev.cursorial.designer.language

import com.intellij.analysis.problemsView.FileProblem
import com.intellij.analysis.problemsView.Problem
import com.intellij.analysis.problemsView.ProblemsCollector
import com.intellij.analysis.problemsView.ProblemsProvider
import com.intellij.openapi.Disposable
import com.intellij.openapi.components.Service
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.openapi.vfs.newvfs.BulkFileListener
import com.intellij.openapi.vfs.newvfs.events.VFileDeleteEvent
import com.intellij.openapi.vfs.newvfs.events.VFileEvent
import dev.cursorial.designer.protocol.DiagnosticItem

/**
 * Publishes XAML diagnostics to the shared Problems view collector, so they appear in the
 * project-wide problems tab and not only under Current File (which is fed by the editor daemon
 * and therefore covers OPEN files only). The annotator forwards each file's fresh results here;
 * entries are diffed per file so fixes retract their problems.
 */
@Service(Service.Level.PROJECT)
class CursorialProblemsPublisher(override val project: Project) : ProblemsProvider, Disposable {

    companion object {
        fun getInstance(project: Project): CursorialProblemsPublisher =
            project.getService(CursorialProblemsPublisher::class.java)
    }

    private val published = HashMap<VirtualFile, List<Problem>>()

    init {
        // A deleted file's problems must not linger in the tab.
        project.messageBus.connect(this).subscribe(VirtualFileManager.VFS_CHANGES, object : BulkFileListener {
            override fun after(events: List<VFileEvent>) {
                for (event in events) {
                    if (event is VFileDeleteEvent)
                        publish(event.file, emptyList())
                }
            }
        })
    }

    /** Replaces the file's published problems with the errors in [items]. */
    @Synchronized
    fun publish(file: VirtualFile, items: List<DiagnosticItem>) {
        val collector = ProblemsCollector.getInstance(project)
        published.remove(file)?.forEach(collector::problemDisappeared)

        val problems = items.filter { it.severity == "error" }.map {
            XamlProblem(this, file, "${it.code ?: "CUR"}: ${it.message}", it.line - 1, (it.column - 1).coerceAtLeast(0))
        }
        if (problems.isNotEmpty()) {
            published[file] = problems
            problems.forEach(collector::problemAppeared)
        }
    }

    override fun dispose() {}
}

private class XamlProblem(
    override val provider: ProblemsProvider,
    override val file: VirtualFile,
    override val text: String,
    override val line: Int,
    override val column: Int,
) : FileProblem
