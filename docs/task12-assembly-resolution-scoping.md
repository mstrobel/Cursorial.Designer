# Task 12 scoping: preview with the USER's Cursorial assemblies

**Status:** Scoping only — read-only survey, no code changed. Builds and tests were not run
(machine build slot occupied); every claim below is cited to source.

The pinned design in `docs/proposal-previewhost-assembly-resolution.md` is the starting point:
this document verifies it against the current code, fills in the parts it left open (seam
mechanics, how the user directory reaches the host, gating UX, test strategy), and cuts the work
into landable slices.

---

## 1. Current host layout

### What the PreviewHost ships

The published payload (`plugin/build/previewHost/`, identical to
`Cursorial.Designer.PreviewHost/bin/Debug/net10.0/`) contains:

| Group | Assemblies |
| --- | --- |
| Designer-owned | `Cursorial.Designer.PreviewHost.dll` (+ apphost), `Cursorial.Designer.Protocol.dll` |
| Cursorial framework (13) | `Cursorial.Animation`, `Cursorial.Core`, `Cursorial.Drawing`, `Cursorial.Rendering`, `Cursorial.Shared`, `Cursorial.UI`, `Cursorial.UI.Bars`, `Cursorial.UI.DataViews`, `Cursorial.UI.Dialogs`, `Cursorial.UI.Hosting.Headless`, `Cursorial.UI.Themes`, `Cursorial.UI.Xaml`, `Cursorial.UI.Xaml.Frontend` |
| Third-party | `Microsoft.Extensions.TimeProvider.Testing` 9.0.0 (Headless's dependency, `../Cursorial/Cursorial.UI.Hosting.Headless/Cursorial.UI.Hosting.Headless.csproj:27`) |

The framework arrives by `ProjectReference` into the sibling checkout
(`Cursorial.Designer.PreviewHost/Cursorial.Designer.PreviewHost.csproj:22-30`;
`$(CursorialRepoRoot)` defined at `Directory.Build.props:36`).

### The Protocol assembly is framework-free by construction

`Cursorial.Designer.Protocol.csproj` has **zero** references of any kind (lines 1-9); its three
source files use only `System.Text.Json.Serialization` (`Commands.cs:1`, `Events.cs:1`,
`Shapes.cs:1`). Serialization is source-generated (`PreviewProtocol.cs:44-50`). **No protocol
type touches a Cursorial type — nothing needs duplicating for the split.**

### The seam already exists in the source layout

Host sources split cleanly by framework-dependence:

- **Framework-free:** `Program.cs` (3 lines — `StdioServer.Run(args)`), `StdioServer.cs`
  (109 lines; usings are `System.Text` + `Cursorial.Designer.Protocol` only, `StdioServer.cs:1-5`).
- **Framework-bound (~4,970 lines):** `PreviewSession.cs` (1197), `EditorServices.cs` (1324),
  `EditorSymbols.cs` (1987), `FrameSerializer.cs` (289), `InputMapper.cs` (119),
  `ValueFormatter.cs` (52).

`StdioServer` touches the framework-bound half at exactly two points, both with Protocol-only
signatures: `new PreviewSession(Emit)` (`StdioServer.cs:80`; ctor takes `Action<PreviewEvent>`,
`PreviewSession.cs:114`) and `session.Execute(command)` (`StdioServer.cs:92`; parameter is
`PreviewCommand`, `PreviewSession.cs:176`). That two-call surface is the launcher/core boundary.

### Where the framework-global state lives

`PreviewSession`'s static ctor initializes process-global framework statics: the resource
provider (`PreviewSession.cs:78`), the live-XAML hook (`:84`), the pinned metadata provider +
`XamlLoader.Shared` capture (`:94-98`), and `XamlSchemaContext.Default.RegisterAssembly` for
Bars/Dialogs (`:104-105`). `EditorServices.MetadataProvider` is the same pin
(`EditorServices.cs:19-30`). Whichever load context loads the core owns ALL of this state — the
structural constraint the proposal identified (`docs/proposal-previewhost-assembly-resolution.md:70-72`).

---

## 2. Process lifecycle (plugin side)

- **Locating the host:** `CursorialDesignerSettings.previewHostDllPath(contextFile)` probes the
  `CURSORIAL_PREVIEWHOST_DLL` env var, then
  `Cursorial.Designer.PreviewHost/bin/Debug/net10.0/Cursorial.Designer.PreviewHost.dll` relative
  to the project root and every ancestor of the file, then the bundled `dotnet/` copy found by a
  code-source walk (`plugin/src/main/kotlin/dev/cursorial/designer/settings/CursorialDesignerSettings.kt:39-58, 65-86`).
- **Spawning:** `PreviewHostProcess` runs `dotnet <hostDll>`
  (`plugin/src/main/kotlin/dev/cursorial/designer/previewer/PreviewHostProcess.kt:163`), with
  crash-restart backoff (`:242-271`). **No arguments beyond the dll path are passed today.**
- **Handshake:** host emits `ready` (`StdioServer.cs:36`); the plugin answers with
  `initialize` + `loadXaml` + `advanceTime(0)`
  (`plugin/src/main/kotlin/dev/cursorial/designer/editor/CursorialPreviewEditor.kt:357-372`).
- **Session↔document mapping:** one host **process per open preview editor**, each bound to one
  XAML file (`CursorialPreviewEditor.kt:307-318`), plus one **project-level language-service
  host** that never sends `initialize` — it serves `analyze`/`complete`/`hover`/`definition`
  across all files in the project
  (`plugin/src/main/kotlin/dev/cursorial/designer/language/CursorialLanguageService.kt:31-44`).
- **User assemblies ride per-command,** never per-process: `loadXaml.assemblies`
  (`Cursorial.Designer.Protocol/Commands.cs:70-76`), and the four editor-service commands
  (`docs/protocol.md:46-59`). `InitializeCommand` carries no assembly information
  (`Commands.cs:40-56`).
- **Rebuild handling (already built, keep it):** the preview editor polls user-assembly stamps
  every 2 s and restarts the host on a stable change (`CursorialPreviewEditor.kt:180-231`); the
  language service restarts when the host dll or a previously-seen user assembly stamp changes
  (`CursorialLanguageService.kt:157-191`). The CLR never reloads a changed dll at the same path
  (`CursorialLanguageService.kt:148-153`), so restart-on-rebuild is the model — and it survives
  this task unchanged.

---

## 3. What "the user's build output" means in Rider terms

### What exists

`UserAssemblyLocator.locate(xamlFile)` walks up from the XAML file to the first directory
containing a `.csproj`, then picks the newest `bin/{Debug,Release}/<tfm>/<ProjectName>.dll`
(`plugin/src/main/kotlin/dev/cursorial/designer/previewer/UserAssemblyLocator.kt:21-45`). Its own
TODO names the replacement: "Rider's workspace model (real target path + configuration)"
(`UserAssemblyLocator.kt:15-16`). A grep for `com.jetbrains.rider`/`workspaceModel`/
`RunnableProject` across `plugin/src` finds **no** project-model API use anywhere — the plugin is
deliberately frontend-only (`docs/architecture.md:26-33`).

### What a user output directory actually contains — verified against Cursorial.Samples

`/Users/mike.strobel/Workspace/Cursorial.Samples/bin/Debug/net10.0/` (a real user app,
ProjectReferencing the framework: `Cursorial.Samples.csproj:17-25`) contains its **own copies**
of `Cursorial.{Animation,Core,Drawing,Rendering,Shared,UI,UI.Bars,UI.Dialogs,UI.Xaml,UI.Xaml.Frontend}`
— but **not** `Cursorial.UI.Themes`, `Cursorial.UI.Hosting.Headless`, `Cursorial.UI.DataViews`,
or `Microsoft.Extensions.TimeProvider.Testing`. The host needs all four (Themes for
`ThemeKeys.ElevationDesktop`, `PreviewSession.cs:12,333`; Headless is the engine). **A mixed
user/bundled graph is therefore structural, not an edge case** — the resolver's bundled fallback
is exercised on every single session, for the host-only assemblies at minimum. (§4 discusses the
residual risk.)

### What would be needed

To learn the active project's TFM, configuration, and real output path, the plugin must consume
Rider's backend project model. Candidate APIs (named, **unverified** — the plugin imports none of
them today, and this is the main investigation cost of slice 6):
`com.jetbrains.rider.projectView.workspace` (workspace-model entities carrying
`RdProjectDescriptor`/output paths), `com.jetbrains.rider.model` (the protocol-generated solution
model), or listening to Rider's build-finished events instead of the current 2 s stamp poll. The
`UserAssemblyLocator.Result` shape can stay; only the implementation swaps.

---

## 4. The ALC design

### The type-identity crux

In .NET, a type's identity is (assembly identity, **load context**). The same assembly file
loaded into two `AssemblyLoadContext`s yields two distinct `System.Type`s: casts fail across the
boundary, and statics — `XamlSchemaContext.Default`, the `XamlModule` hooks, theme state — exist
once **per context**. The core compiles directly against `Cursorial.*`
(`PreviewSession.cs:1-13`), so wherever the core's code loads, its `Cursorial.*` references bind
in **that** context. Any design where the core and the user's assemblies resolve `Cursorial.UI`
in different contexts produces two `Button` types, two schema contexts, and
`InvalidCastException`s that are strictly harder to diagnose than today's `FileLoadException`
(`docs/proposal-previewhost-assembly-resolution.md:63-69`).

### The options

**(a) Reflection-only seam** — core stays in the default context, compiled against no Cursorial,
all framework access via reflection into the user ALC. Rejected: ~4,970 lines of host code make
direct framework calls (§1), and `EditorServices`/`EditorSymbols` exist *because* compiling
against the framework gives real metadata. This is a rewrite wearing an architecture costume.

**(b) Launcher/core split — core loaded INTO the user ALC** *(the proposal's design, and the
recommendation)*. A framework-free launcher owns the process, the stdio pipe, and ALC
construction; the core (everything framework-bound, unchanged) loads into a single custom ALC
together with the framework and the user's assemblies. Exactly one `Cursorial.*` per identity;
every framework static initializes inside the ALC; the user's copies win by resolver preference.

**(c) `AssemblyDependencyResolver` forwarding** — host stays in default context, user assemblies
load into a child ALC with `Cursorial.*` forwarded. Already analyzed and rejected in the proposal
(`proposal-previewhost-assembly-resolution.md:57-69`): forwarding to the host's copies previews a
stale framework (inverts the requirement); forwarding to the user's copies creates the
two-frameworks split described above, because the host binds `Cursorial.*` into the default
context before any user assembly loads (`PreviewSession.cs:104-105`).

### Recommendation: (b), with these mechanics pinned

1. **Project layout.** `Cursorial.Designer.PreviewHost` keeps its name (so
   `CursorialDesignerSettings.DEFAULT_HOST_RELATIVE_PATH` and the bundled-dll probe,
   `CursorialDesignerSettings.kt:27-28, 80`, stay valid) and becomes the launcher: `Program.cs`,
   `StdioServer.cs`, new resolver/gate code; references **only** Protocol. New
   `Cursorial.Designer.PreviewHost.Core` takes the six framework-bound files verbatim (keep the
   namespace to minimize the diff) plus the framework `ProjectReference`s and the
   `InternalsVisibleTo` grant (`Cursorial.Designer.PreviewHost.csproj:32-34` moves, target
   unchanged). The launcher gets the Core's output copied without a compile-time reference —
   `<ProjectReference ... ReferenceOutputAssembly="false" OutputItemType="none">` plus a copy
   target, so a stray `typeof(PreviewSession)` in launcher code is a **compile error**, not a
   silent default-context bind.

2. **Protocol unification — the load-bearing detail.** The launcher's ALC `Load()` override
   returns **null** for `Cursorial.Designer.Protocol`, which falls resolution through to
   `AssemblyLoadContext.Default`, where the launcher already loaded it. That is the mechanism by
   which the launcher's `Action<PreviewEvent>` delegate and the `PreviewCommand` instances it
   passes into the core are the *same types* on both sides of the boundary. For everything else,
   `Load()` probes the **user output directory first**, then the launcher's own directory (the
   bundled copies) — covering both the framework graph and host-only assemblies the user's output
   lacks (§3).

3. **The seam type.** Replace `new PreviewSession(Emit)` (`StdioServer.cs:80`) with a factory
   resolved from the Core assembly. Recommended shape: a small
   `ISessionCore : IDisposable { void Execute(PreviewCommand command); }` interface declared in
   Protocol (documented as a process-internal seam, not a wire shape), implemented by a Core
   entry type found by well-known name via `alc.LoadFromAssemblyPath(coreDll)`. Interface
   dispatch after one reflective activation; no per-command reflection.

4. **One code change inside the core:** `RegisterAssemblies` must stop using
   `Assembly.LoadFrom` (`PreviewSession.cs:557`) — `LoadFrom` **always loads into the default
   ALC** regardless of the caller's context, which would re-split the framework. It becomes
   `AssemblyLoadContext.GetLoadContext(typeof(PreviewSession).Assembly)!.LoadFromAssemblyPath(path)`,
   landing user assemblies in the same ALC as the core. (In the in-process test harness that
   context is the default context, so tests keep working unchanged.)

5. **How the user directory reaches the launcher.** The ALC must exist before the core's first
   type load, but assemblies arrive per-command today (§2). Recommended: a launcher CLI argument
   (`dotnet <launcher.dll> --user-dir <path>`) supplied at spawn — both spawn sites already have
   the context file in hand (`CursorialPreviewEditor.kt:307`,
   `CursorialLanguageService.kt:157-158`) and `UserAssemblyLocator` already derives the directory.
   No output located → no `--user-dir` → bundled-only ALC, exactly today's behavior. The
   alternative (launcher defers core load until the first assemblies-bearing command and derives
   the directory from it) needs no Kotlin change but adds a pre-core command-buffering state
   machine to the launcher; see gate G3.

6. **Non-collectible, restart-on-rebuild.** The proposal pins unloadability as a non-goal
   (`proposal-previewhost-assembly-resolution.md:137-138`), and the plugin's restart machinery
   already exists on both host paths (§2). Nothing new to build; see gate G2.

7. **Residual risk — mixed vintages.** Host-only assemblies (`UI.Themes`,
   `UI.Hosting.Headless`, `UI.DataViews`, `TimeProvider.Testing`) always come from the bundle;
   a user framework *newer* than the bundle mixes vintages within one graph. The proposal's own
   caveat ("at least as new is necessary but not sufficient",
   `proposal-previewhost-assembly-resolution.md:114-116`) applies. Mitigation: report the
   per-source resolution in the ready payload (§5) so failures are attributable; see gate G5 for
   whether to go further.

8. **Thread affinity is orthogonal but must be verified under test.** `UIHeadlessHost.Create`
   keys off the calling thread, not the load context; the launcher's command loop thread still
   constructs the session (`StdioServer.cs:7-11, 80`). The E2E test in slice 1 is the
   verification the proposal asks for (`proposal-previewhost-assembly-resolution.md:132-134`).

---

## 5. Version gating

### Where v0.5.0 comes from

The sibling checkout **is** 0.5.0 (`../Cursorial/Cursorial.Core/Cursorial.Core.csproj:18`; tags
`v0.4.0` and `v0.5.0` exist in the framework repo), and the release recipe already pins plugin
builds at a v0.5.0 worktree (`plugin/build.gradle.kts:86-90`). 0.5.0 is the first version where
(a) the XAML generator no longer emits the module initializer that stomps the process-default
metadata provider — "user apps built against Cursorial <= 0.4.0 install their closed-set
provider… (retired since…)" (`PreviewSession.cs:88-93`, `:536-543`, `EditorServices.cs:19-30`) —
and (b) the seams the host requires exist (`XamlModule.LiveXamlSource`/`LiveXamlLoader`,
pull-discovered entry-assembly provider, `UIProperties.Inheriting` from framework PR #17,
`PreviewSessionTests.cs:528-531`).

### What the gate checks (recommended)

Compare `AssemblyName.GetAssemblyName(<userDir>/Cursorial.Core.dll).Version` — a metadata-only
read, no load, safe in the launcher before ALC construction — against the bundled
`Cursorial.Core`'s version (`<Version>0.5.0</Version>` ⇒ AssemblyVersion `0.5.0.0`). Rule per the
proposal (`proposal-previewhost-assembly-resolution.md:100-116`): user ≥ bundled → user's copies
win; user < bundled → bundled fallback **plus a diagnostic, never silent**. Under this rule the
"minimum v0.5.0" is simply the bundled version at this feature's first release — the floor
floats upward with every plugin release instead of a constant going stale (gate G4).

### UX when the gate fails (or falls back)

- **Wire:** extend `ReadyEvent` (`Cursorial.Designer.Protocol/Events.cs:31-36`) with optional
  `frameworkVersion`, `frameworkSource` (`"user"`/`"bundled"`), `frameworkPath`, and
  `fallbackReason` — additive fields, no protocol bump (`docs/protocol.md:154-156`). A `ready`
  payload beats `error`/`log` events: `LogEvent`s only reach the IDE log
  (`CursorialPreviewEditor.kt:285`), and an `ErrorEvent` renders "Previewer error: …" for what is
  a working-but-degraded state.
- **Plugin:** the status strip narrates, consistent with existing behavior ("Project output
  changed — restarting preview…", `CursorialPreviewEditor.kt:226`): e.g. *"Previewing with the
  designer's Cursorial 0.5.0 — this project targets the older 0.4.0."* with detail in the
  tooltip. Whether the no-build-output case deserves louder treatment is gate G1; the current
  string for it lives at `UserAssemblyLocator.kt:38`.
- **Launch failures:** the related diagnostics fix
  (`proposal-previewhost-assembly-resolution.md:140-146`): any non-JSON stdout before the first
  valid protocol event is a launch failure and must be surfaced with its text intact, replacing
  the "Dropping malformed event" swallow at `PreviewHostProcess.kt:220-226`. Independent slice.

---

## 6. Testing

### What exists

- **`PreviewSessionTests`** — 41 facts, in-process against `PreviewSession` on the test thread
  (which becomes the UI thread, `PreviewSessionTests.cs:21-33`), asserting real rendered frames.
  Covers load/reload, diagnostics, rollback, hit-test/properties/provenance, themes, input,
  capability profiles.
- **`EditorServiceTests`** — 93 facts, including the provider-hijack regression that motivates
  the pinned metadata provider (parallelization disabled for it, `TestAssemblyInfo.cs:1-7`).
- **`ProtocolTests`** — 6 wire round-trip facts.
- **`EndToEndTests`** — one full stdio session spawning the real host from the test output
  directory (`EndToEndTests.cs:14-15, 18-96`).

All in-process tests run in the default context and keep working against Core with only a
`ProjectReference` retarget (`Cursorial.Designer.PreviewHost.Tests.csproj:22-24`). (Aside:
`CLAUDE.md`'s "runs 18 tests" is long stale — 141 facts today.)

### What the split must add

- **ALC boundary test (in-process, no spawn):** construct the launcher's ALC in a test, load the
  Core into it, pass a `PreviewCommand` in and receive `PreviewEvent`s out. This *directly*
  proves Protocol type unification across the boundary — the highest-risk mechanism.
- **Fixture user project** (new csproj, e.g. `Cursorial.Designer.PreviewHost.Tests.UserApp`):
  ProjectReferences the framework like Cursorial.Samples does, defines one custom control and a
  viewmodel. Built by the normal test build; referenced with `ReferenceOutputAssembly="false"`
  so its output exists at a known path. E2E: spawn the launcher with `--user-dir` at the fixture
  output; assert `ready.frameworkSource == "user"` and `frameworkPath` under the fixture dir;
  `loadXaml` a document using the custom control; assert it renders and `hitTest` reports its
  type name.
- **Fake out-of-date framework** (new fixture csproj with `AssemblyName=Cursorial.Core`,
  `AssemblyVersion 0.1.0.0`): point `--user-dir` at it; assert bundled fallback + the
  `fallbackReason` payload. No real old framework build required.
- **Honest limitation:** the proposal's divergent-vintage regression
  (`proposal-previewhost-assembly-resolution.md:171-173`) needs two genuinely different framework
  builds; deterministic builds (`Directory.Build.props:30`) make same-source copies identical.
  Covered indirectly by the source/version assertions above; a true divergence run needs a second
  framework worktree build — CI-optional/manual, not a checked-in test.
- **Not testable without Rider:** workspace-model discovery, strip/banner UX, spawn wiring —
  manual `runIde` verification. Everything else in this task is testable headlessly.

---

## 7. Blast radius and phasing

**Touched:** 2 csproj edits + 2-3 new csprojs (.NET); ~250 new launcher lines; one-line
`LoadFrom` change in `PreviewSession.cs:557`; additive `ReadyEvent` fields + `ISessionCore` in
Protocol; test retarget + new fixtures; `publishPreviewHost` publishes launcher + core
(`plugin/build.gradle.kts:69-99`; the flat one-directory layout can stay — the resolver probes
its own directory last); Kotlin spawn args (`PreviewHostProcess.kt:163` + both call sites),
launch-failure diagnostics, ready-payload strip UX, Kotlin `ReadyEvent` DTO
(`ProtocolMessages.kt:229-232`); docs (`architecture.md` process model + `:43-45` lifecycle note,
`protocol.md`, the proposal's status line, `CLAUDE.md` facts).

### Slices (each independently landable)

1. **Launcher/core split, bundled-only ALC — the type-identity proof.** Split the projects;
   launcher builds an ALC resolving everything to its own directory (no user dir yet), Protocol
   pinned to the default context via `Load()`→null; `LoadFrom` → context-local
   `LoadFromAssemblyPath`. Zero user-visible behavior change. *Tests:* all 141 existing facts
   green (in-process against Core; E2E against the launcher — the full pipeline running with the
   framework in a child ALC and commands/events crossing the boundary **is** the end-to-end
   identity proof), plus new `AlcBoundaryTests`.
2. **Framework report on `ready`.** Additive `frameworkVersion`/`frameworkSource`/
   `frameworkPath`/`fallbackReason`; Kotlin DTO + strip line. *Tests:* protocol round-trip;
   E2E asserts `frameworkSource == "bundled"`.
3. **User-directory preference.** `--user-dir` CLI arg; resolver probes it first; Kotlin passes
   it from both spawn sites. *Tests:* the UserApp fixture E2E (renders a user-defined control,
   `frameworkPath` under the fixture output — the task's headline demo).
4. **Version gate + fallback diagnostic.** Metadata-only version compare in the launcher;
   fallback path + `fallbackReason`; strip UX. *Tests:* fake-old-`Cursorial.Core` fixture E2E.
5. **Launch-failure diagnostics.** Pre-`ready` non-JSON stdout surfaced intact
   (`PreviewHostProcess.kt:220-226`). Independent of all other slices; cheap; do early.
6. **Workspace-model discovery.** Replace the `UserAssemblyLocator` heuristic behind its
   existing `Result` shape (`UserAssemblyLocator.kt:47-50`); investigation-first (§3). Manual
   `runIde` verification.
7. **Docs + release recipe.** Proposal → implemented; `architecture.md`/`protocol.md`/
   `CLAUDE.md`; `publishPreviewHost` notes on what "bundled" now means (a genuine fallback,
   `proposal-previewhost-assembly-resolution.md:124-126`).

---

## 8. Mike-gated questions

- **G1 — No build output: how loud is the bundled fallback?** The proposal keeps the bundled
  framework as a genuine fallback (`:124-126`), and today's locator already emits "Project 'X'
  has no built output — build it to preview its types" (`UserAssemblyLocator.kt:38`) while the
  preview *renders anyway* with built-ins. Options: keep that quiet strip line; a persistent
  editor banner; or refuse to preview user-typed documents until built. Recommended: keep the
  fallback + strip line (the preview of framework-only markup is genuinely useful), but this is a
  product-feel call.
- **G2 — Rebuild = restart (status quo) or collectible-ALC hot reload?** The proposal pins
  non-collectibility to avoid teardown bugs (`:137-138`) and the restart plumbing already exists
  on both host paths (§2). Recommended: restart. Hot reload would resurrect a class of
  cross-context/unload bugs for ~1 s of saved latency.
- **G3 — How the user directory reaches the launcher.** CLI arg at spawn (recommended; small
  Kotlin change, dumb launcher) vs deferring core load until the first assemblies-bearing
  command (no Kotlin change, stateful launcher). Related sub-question: one language-service host
  serves a whole project today — in a solution mixing projects on different Cursorial versions,
  is first-wins-per-process acceptable, or does the service become per-output-directory?
- **G4 — Gate comparand.** Bundled `Cursorial.Core` version as a floating floor (recommended;
  never goes stale) vs a hard-coded v0.5.0 minimum (more legible, but wrong the day the bundled
  host uses a 0.6.0 API).
- **G5 — Mixed vintages (user newer than bundle).** Host-only assemblies always come from the
  bundle (§3), so user-0.6.0 + bundled-0.5.0-Headless graphs will exist. Accept + report via the
  ready payload (recommended), or additionally probe the framework checkout's own build outputs
  in the ProjectReference/sibling-dev scenario (helps exactly the workflow this repo lives in,
  but adds a second discovery heuristic)?
