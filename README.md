# Cursorial.Designer

A JetBrains Rider plugin providing a **XAML editor with a live visual designer** for
[Cursorial](https://github.com/mstrobel/Cursorial) — the .NET terminal UI framework whose XAML
renders to a character-cell grid.

The preview is not a screenshot and not a terminal emulator: an out-of-process host runs the
*real* Cursorial pipeline (layout, styling, binding, themes) headlessly and streams composited
cell-grid frames to the IDE, which paints them and sends back input, hit-tests, and property
queries. Because the preview host executes the same `XamlLoader` and `Cursorial.UI` code that
ships, the designer cannot diverge from production rendering. See `docs/architecture.md`.

## Layout

| Path | What it is |
| --- | --- |
| `Cursorial.Designer.Protocol/` | Wire shapes for the plugin ⇄ preview-host protocol (`docs/protocol.md`) |
| `Cursorial.Designer.PreviewHost/` | net10.0 worker: headless Cursorial host driven over stdio |
| `Cursorial.Designer.PreviewHost.Tests/` | xUnit suite, including a spawn-the-real-process end-to-end test |
| `plugin/` | The Rider plugin (Kotlin, IntelliJ Platform) — see `plugin/BUILDING.md` |
| `docs/` | Architecture and protocol documentation |

## Building

Requires the .NET 10 SDK and a sibling checkout of the Cursorial repo
(`../Cursorial` by default; override with `-p:CursorialRepoRoot=...`):

```sh
dotnet test Cursorial.Designer.PreviewHost.Tests
```

The plugin half builds separately with Gradle — see `plugin/BUILDING.md`.

## Status

Phase 1 (preview engine) is functional: initialize/load/resize/input/hit-test/properties/theme
over newline-delimited JSON, with diagnostics carrying Cursorial's stable `CURxxxx` codes and
1-based positions. The Rider plugin is scaffolded and under active development. Not yet carried:
image fragments, element→source jump (needs a Cursorial-side seam), design-time data, completion.

## License

Apache-2.0 — see [LICENSE](LICENSE).
