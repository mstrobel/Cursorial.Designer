package dev.cursorial.designer.previewer

import com.intellij.openapi.vfs.VirtualFile
import java.io.File

/**
 * Locates the built output assembly of the project containing a XAML file, so the preview host
 * can load and register the user's own control types.
 *
 * v1 heuristic: walk up from the file to the first directory containing a `.csproj`, then pick
 * the newest `<ProjectName>.dll` under `bin/{Debug,Release}/<tfm>/`. Dependencies resolve from
 * the same output directory (SDK-style builds copy references), and framework assemblies bind to
 * the ones the host already has loaded.
 *
 * TODO: replace with Rider's workspace model (real target path + configuration) and restart the
 *  host on project rebuild — the heuristic requires a prior build and can pick a stale dll.
 */
object UserAssemblyLocator {

    /** Returns the discovery result: assembly paths to load, or a human-readable problem. */
    fun locate(xamlFile: VirtualFile): Result {
        var directory = xamlFile.parent
        while (directory != null) {
            val csproj = directory.children.firstOrNull { !it.isDirectory && it.extension == "csproj" }
            if (csproj != null) {
                val projectName = csproj.nameWithoutExtension
                val newest = listOf("Debug", "Release")
                    .flatMap { configuration ->
                        File(directory!!.path, "bin/$configuration").listFiles()?.toList().orEmpty()
                    }
                    .filter(File::isDirectory)
                    .mapNotNull { tfmDir -> File(tfmDir, "$projectName.dll").takeIf(File::isFile) }
                    .maxByOrNull(File::lastModified)

                return if (newest != null) {
                    Result(assemblies = listOf(newest.absolutePath))
                } else {
                    Result(problem = "Project '$projectName' has no built output — build it to preview its types.")
                }
            }
            directory = directory.parent
        }

        return Result() // no containing project: core controls only, which is fine
    }

    data class Result(
        val assemblies: List<String> = emptyList(),
        val problem: String? = null,
    )
}
