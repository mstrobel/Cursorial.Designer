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
 * TODO: replace with Rider's workspace model (real target path + configuration, e.g.
 *  com.jetbrains.rider.projectView.workspace) and Rider's build-finished events instead of the
 *  2s stamp poll — the heuristic requires a prior build and can pick a stale dll. The plugin is
 *  deliberately frontend-only today (docs/architecture.md), so no backend project-model API is
 *  available without taking that dependency; both [locate] and [locateOutputDirectory] swap
 *  implementations behind their existing shapes when it lands.
 */
object UserAssemblyLocator {

    /**
     * The build-output DIRECTORY containing the located assembly — the value of the preview
     * host's `--user-dir` spawn argument. Null when no built output exists (the host is then
     * spawned without the argument: bundled-only, plus the not-built cue).
     */
    fun locateOutputDirectory(xamlFile: VirtualFile): File? =
        locate(xamlFile).assemblies.firstOrNull()?.let { File(it).parentFile }

    /** Returns the discovery result: assembly paths to load, or a human-readable problem. */
    fun locate(xamlFile: VirtualFile): Result {
        var directory = xamlFile.parent
        while (directory != null) {
            val csproj = directory.children.firstOrNull { !it.isDirectory && it.extension == "csproj" }
            if (csproj != null) {
                val projectName = csproj.nameWithoutExtension
                // The output file honors <AssemblyName> when the csproj declares one (e.g. project
                // Cursorial.CLI building `curio.dll`); fall back to the project-name convention.
                val assemblyName = runCatching {
                    Regex("<AssemblyName>\\s*([^<\\s][^<]*?)\\s*</AssemblyName>")
                        .find(String(csproj.contentsToByteArray(), Charsets.UTF_8))
                        ?.groupValues?.get(1)
                }.getOrNull()
                val candidates = listOfNotNull(assemblyName, projectName).distinct()

                val newest = listOf("Debug", "Release")
                    .flatMap { configuration ->
                        File(directory!!.path, "bin/$configuration").listFiles()?.toList().orEmpty()
                    }
                    .filter(File::isDirectory)
                    .flatMap { tfmDir ->
                        // A PublishAot/RID-specific project builds one level deeper (bin/<cfg>/<tfm>/<rid>/);
                        // scan the tfm directory AND its RID subdirectories. `ref/` holds implementation-less
                        // reference assemblies — never loadable content.
                        val searchDirs = listOf(tfmDir) +
                            tfmDir.listFiles().orEmpty().filter { it.isDirectory && it.name != "ref" }
                        searchDirs.mapNotNull { dir ->
                            candidates.firstNotNullOfOrNull { name -> File(dir, "$name.dll").takeIf(File::isFile) }
                        }
                    }
                    .maxByOrNull(File::lastModified)

                return if (newest != null) {
                    Result(assemblies = listOf(newest.absolutePath))
                } else {
                    Result(problem = "Project '$projectName' has no built output " +
                        "(looked for ${candidates.joinToString(" / ") { "$it.dll" }}) — build it to preview its types.")
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
