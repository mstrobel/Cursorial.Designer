# Building the Cursorial Designer Rider plugin

Frontend-only IntelliJ Platform plugin (Kotlin) targeting **JetBrains Rider 2026.1.1**
(build branch `261`). No ReSharper/.NET backend part in this phase.

## Versions

| Component                       | Version   | Why |
|---------------------------------|-----------|-----|
| Gradle (wrapper)                | 9.6.1     | Runs on JDK 17–26 (this machine's default JVM is JDK 26); same wrapper AvaloniaRider pairs with this plugin toolchain. |
| intellij-platform-gradle-plugin | 2.17.0    | Latest 2.x as of 2026-07. |
| Kotlin                          | 2.3.20    | Latest stable as of 2026-07 (released 2026-03-16). |
| Rider SDK target                | 2026.1.1  | Latest stable Rider (released 2026-04-27). Resolved with `useInstaller = false` + an explicit `jetbrainsRuntime()` dependency, mirroring AvaloniaRider. |
| Java toolchain (compilation)    | 21        | The 2026.x IntelliJ Platform requires Java 21 bytecode. Auto-provisioned by the `foojay-resolver-convention` settings plugin — no local JDK 21 install needed. |
| JSON                            | Gson      | Bundled with the IntelliJ Platform; no extra dependency. |

## Prerequisites

- Any JDK 17–26 on `PATH` to launch Gradle (JDK 26 from Homebrew works; the JDK 21
  compile toolchain is downloaded automatically on first build).
- Network access on first build: Gradle distribution (~140 MB), JDK 21 toolchain,
  and the Rider 2026.1.1 SDK (>1 GB) are downloaded and cached under `~/.gradle`.
- To *run* the preview, the .NET 10 SDK and a built
  `Cursorial.Designer.PreviewHost.dll` (see below).

## Build

```sh
cd plugin
./gradlew build          # compile + verify plugin structure
./gradlew buildPlugin    # produce the distributable zip in build/distributions/
./gradlew runIde         # launch a sandboxed Rider 2026.1.1 with the plugin
```

The wrapper jar and scripts were fetched from the `gradle/gradle` repository at tag
`v9.6.1`. If you ever need to re-bootstrap them from scratch, run (with any Gradle
installation): `gradle wrapper --gradle-version 9.6.1`.

## Locating the PreviewHost

The preview editor launches `dotnet <PreviewHost dll>`. The dll is located by
`dev.cursorial.designer.settings.CursorialDesignerSettings`, in order:

1. the `CURSORIAL_PREVIEWHOST_DLL` environment variable;
2. `<project root>/host/Cursorial.Designer.PreviewHost/bin/Debug/net10.0/Cursorial.Designer.PreviewHost.dll`.

This is a v1 stub; a real settings page is a TODO.

## How the editor activates

A `.xaml` file whose first 8 KB contain the xmlns marker `cursorial.dev` gets the split
editor (`FileEditorProvider` + `TextEditorWithPreview`, policy `HIDE_DEFAULT_EDITOR`).

## STATUS — what was verified, and what was not

*(This section is honest by design; update it as the situation changes.)*

**Verified:**

- Version research done 2026-07-10 with live web searches: intellij-platform-gradle-plugin
  2.17.0 is current; Rider 2026.1.1 is the latest stable; Gradle 9.6 supports JDK 17–26;
  Kotlin 2.3.20 is the latest stable; foojay-resolver-convention 1.0.0 is current.
- Build-script shape (Rider SDK dependency block, `useInstaller = false`,
  `jetbrainsRuntime()`, `kotlin.stdlib.default.dependency=false`) copied from
  AvaloniaRider's real `build.gradle.kts`/`libs.versions.toml` (fetched from GitHub,
  main branch, which pairs IJPGP 2.17.0 with Gradle 9.6.1).
- `TextEditorWithPreview` public constructor signature and its child-editor disposal
  behavior read directly from intellij-community sources (master).
  `TextEditorWithPreviewProvider` is `@ApiStatus.Internal` and therefore NOT used;
  the provider composes `TextEditorProvider` + `TextEditorWithPreview` manually.
- `OSProcessHandler` usage (`BaseOutputReader.Options.forMostlySilentProcess()`,
  `ProcessListener.onTextAvailable`) read from AvaloniaRider's
  `AvaloniaPreviewerProcess.kt`.
- gradle-wrapper.jar/gradlew fetched from the gradle/gradle repo at tag v9.6.1 (valid zip).
- BUILD RESULT: `./gradlew buildPlugin` **succeeds** (2026-07-10, JDK 21 toolchain on a
  JDK 26 host, Rider 2026.1.1 SDK): `build/distributions/cursorial-designer-0.1.0.zip`.
  One compile error was found and fixed after scaffolding: `hostListener` in
  `CursorialPreviewEditor.kt` was declared below the `init` block that referenced it
  (Kotlin initializes in declaration order); the declaration now precedes `init`.

**Known sandbox quirk — "External file changes sync might be slow":** the `useInstaller = false`
SDK comes from the multi-platform (Linux-tar-layout) distribution, and Gradle's archive
transform drops the executable bit on the bundled macOS natives. If the sandbox IDE shows this
notification, restore the bits and restart it:

```sh
chmod +x ~/.gradle/caches/*/transforms/*/transformed/riderRD-*/bin/mac/*/fsnotifier
```

(Harmless to ignore for preview work — the plugin reads the in-editor document, not the
filesystem. Recurs whenever the SDK cache is re-transformed, e.g. after a version bump.)

**Sandbox settings persistence:** the sandbox keeps its config between runs, but
`prepareSandbox` rebuilds the *plugins* directory every run — anything installed inside the
sandbox (e.g. a keymap plugin) silently vanishes on the next launch, reverting derived custom
keymaps with it. The build seeds the developer's real Rider keymaps (once) and re-seeds
`XWinKeymap` every prepare (see `prepareSandbox` in build.gradle.kts). Also: quit the sandbox
IDE with Cmd+Q rather than Ctrl+C on Gradle, or freshly-changed settings may never flush to
disk. Verified persisting across shutdown/relaunch 2026-07-11.

## Packaging for local installs

`./gradlew buildPlugin` produces `build/distributions/cursorial-designer-<version>.zip` — a
single OS-independent distribution. The build `dotnet publish`es the PreviewHost
(framework-dependent, portable) and bundles it under `dotnet/` inside the plugin, so the zip
works on machines that do not have this repository.

Install on any machine via **Settings → Plugins → ⚙ → Install Plugin from Disk…** and restart.

Target machine requirements:

- Rider 2026.1+ (`sinceBuild = 261`).
- A .NET 10 runtime with `dotnet` on the PATH — the plugin launches the bundled host as
  `dotnet Cursorial.Designer.PreviewHost.dll` (one portable publish covers Windows/Linux/macOS).

Host resolution order at runtime: the `CURSORIAL_PREVIEWHOST_DLL` env override → a Debug build
found in the opened solution or its ancestors (the dev loop, self-refresh included) → the copy
bundled in the plugin installation. Dev machines with this repo keep using their fresh local
build; everyone else runs the bundled Release host.

**Not verified / review before shipping:**

- `JBColor.isBright()` as the INITIAL light/dark theme signal in `CursorialPreviewEditor`;
  no reaction to runtime IDE theme changes yet (the in-preview toggle covers the rest).
- Gson maps events reflectively; fields the host omits will surface as `null` even in
  non-null Kotlin declarations (documented in `ProtocolMessages.kt`).
- `sinceBuild = "261"` / default `untilBuild` (261.*) — widen once tested on newer IDEs.
