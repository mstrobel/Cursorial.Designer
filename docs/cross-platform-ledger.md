# Cross-platform compatibility ledger

Things to double-check on Windows and Linux before calling the plugin portable. Development
happens on macOS; entries move to "verified" only after being exercised on the named OS.

## Open items

| Area | Concern | Notes |
| --- | --- | --- |
| Go-to-definition | PDB document paths use the **build machine's** separators and casing | Handled: normalized via `FileUtil.toSystemIndependentName` before the VFS lookup; still needs a real Windows verification (case-insensitive drive letters, `C:\` prefixes). |
| Go-to-definition | Deterministic/CI-built assemblies record `/_/`-style mapped paths | Non-navigable by design (the handler requires the file to exist); consider SourceLink resolution later. |
| XML doc lookup | `Path.ChangeExtension(dll, ".xml")` assumes the doc file sits next to the dll with matching case | Linux is case-sensitive: a `Foo.XML` would be missed. Acceptable; note only. |
| Host discovery | `previewHostDllPath` ancestor walk + `dotnet` on PATH | Windows: `dotnet.exe` resolution via `GeneralCommandLine` should be fine; verify no hardcoded `/` joins in the locator. |
| Assembly cache keys | `XmlDocCache` keys are `OrdinalIgnoreCase` paths | Correct for Windows, harmless on Unix — but the `_registeredAssemblies` set in `PreviewSession` is case-**sensitive**; a re-cased path would double-load on Windows. Low impact (LoadFrom dedupes by identity). |
| Line endings | Editor services assume `\n`-normalized text | True for IntelliJ document text (both directions); raw-file consumers (E2E stdio tests, external tools) must not send `\r\n` and expect column fidelity on the same line. |
| Gradle sandbox seeding | `prepareSandbox` seeds keymaps/plugins from the **macOS** Rider config location (`~/Library/Application Support/JetBrains/…`) | Dev-convenience only, but will silently no-op on Windows/Linux; gate by OS and add the platform config dirs if anyone else runs `runIde`. |
| Gradle daemon | `./gradlew --stop` after SSH-context builds (graphics-environment poisoning) | macOS-specific quirk of this dev setup; unknown whether the Linux equivalent (headless DISPLAY) bites the same way. |
| Preview host spawn | stderr/stdout pipes and process-tree kill | `Process.Kill(entireProcessTree: true)` in tests and plugin-side termination: verify orphan cleanup on Windows job objects vs Unix process groups. |
| File watching | Rebuild watcher polls `lastModified` timestamps every 2 s | FAT/exFAT (2 s mtime granularity) could alias rapid rebuilds; NTFS/ext4 fine. |

## Verified

*(nothing yet — populate as Windows/Linux runs happen)*
