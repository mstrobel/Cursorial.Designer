package dev.cursorial.designer.settings

import com.intellij.openapi.components.Service
import com.intellij.openapi.components.service
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths

/**
 * Locates the Cursorial.Designer.PreviewHost binary.
 *
 * v1 stub: checks an environment variable, then a hardcoded workspace-relative default.
 *
 * TODO: replace with a real settings page (Configurable + PersistentStateComponent) that
 *  lets the user point at a PreviewHost build, and eventually auto-discover/auto-build the
 *  host from the loaded solution like AvaloniaRider resolves its previewer from NuGet.
 */
@Service(Service.Level.PROJECT)
class CursorialDesignerSettings(private val project: Project) {

    companion object {
        const val ENV_PREVIEW_HOST_DLL = "CURSORIAL_PREVIEWHOST_DLL"

        /** Path of the PreviewHost dll relative to the project root, used as the default. */
        const val DEFAULT_HOST_RELATIVE_PATH =
            "Cursorial.Designer.PreviewHost/bin/Debug/net10.0/Cursorial.Designer.PreviewHost.dll"

        fun getInstance(project: Project): CursorialDesignerSettings = project.service()
    }

    /**
     * Returns the PreviewHost dll to launch, or null if none could be found.
     * Order: environment variable override, the project root, then every ancestor directory of
     * [contextFile] — so previews work regardless of whether the repo root, a subfolder, or a
     * loose file was opened.
     */
    fun previewHostDllPath(contextFile: VirtualFile? = null): Path? {
        System.getenv(ENV_PREVIEW_HOST_DLL)?.let { override ->
            val path = Paths.get(override)
            if (Files.isRegularFile(path)) return path
        }

        val roots = sequence {
            project.basePath?.let { yield(it) }
            var directory = contextFile?.parent
            while (directory != null) {
                yield(directory.path)
                directory = directory.parent
            }
        }

        return roots
            .map { Paths.get(it, DEFAULT_HOST_RELATIVE_PATH) }
            .firstOrNull { Files.isRegularFile(it) }
    }
}
