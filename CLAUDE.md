# CLAUDE.md

Guidance for Claude Code sessions in this repository.

## What this repo is

A JetBrains Rider plugin (XAML editor + live visual designer) for the Cursorial terminal UI
framework. Two halves: .NET (`Cursorial.Designer.Protocol`, `Cursorial.Designer.PreviewHost` +
tests) and the IntelliJ-platform plugin (`plugin/`, Kotlin + Gradle). Read
`docs/architecture.md` first; the wire format is `docs/protocol.md` and its normative C# shapes
live in `Cursorial.Designer.Protocol`.

## The sibling framework repo — rules

- The Cursorial framework is consumed from a **sibling checkout** at `../Cursorial` via
  `ProjectReference` (`$(CursorialRepoRoot)` in `Directory.Build.props`, overridable with
  `-p:CursorialRepoRoot=...`).
- **Never modify the main Cursorial checkout.** When designer work needs a framework-side change,
  create a branch in a git worktree inside the Cursorial repo (`git worktree add
  .worktrees/<branch> -b <branch>`) and make the change there.
- The preview host builds on `Cursorial.UI.Hosting.Headless` (headless `UIHeadlessHost`) — it is the
  framework's own test substrate, deliberately unpublished on NuGet; the ProjectReference is the
  sanctioned way in.
- Key framework facts that shape this repo: `UIHeadlessHost.Create` makes the *calling thread* the UI
  thread and every framework API the session touches (`HitTest`, `GetValue`, theme setters) is
  affine to it; `XamlLoader` with `XamlDiagnosticMode.CollectAll` never throws on parse
  diagnostics; `XamlSchemaContext.Default.RegisterAssembly` is required before user-assembly
  types resolve; element identity is reference-based (no Tag/Uid on `UIElement`).

## Building and testing

```sh
dotnet test Cursorial.Designer.PreviewHost.Tests   # builds everything .NET, runs 18 tests
```

The test suite includes an end-to-end test that spawns the real PreviewHost process and drives
the stdio protocol. The plugin builds with `./gradlew build` inside `plugin/` (JDK 17/21
toolchain; see `plugin/BUILDING.md`).

## Conventions

- Match the sibling Cursorial repo: its `.editorconfig` and `global.json` are copied here
  verbatim; C# is latest-language, nullable-enabled, `var`-preferred; csproj XML uses 4-space
  indentation; "UI" is fully capitalized in type names.
- Tests are xUnit, headless-first, and assert against real rendered frames — not mocks. When
  adding protocol messages, add a round-trip test and bump coverage in `PreviewSessionTests`.
- Protocol changes: additive fields don't bump `PreviewProtocol.Version`; breaking shape changes
  do. Update `docs/protocol.md` and the Kotlin DTOs in `plugin/` together with the C# shapes.
