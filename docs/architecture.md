# Architecture

Cursorial.Designer is a JetBrains Rider plugin providing a XAML editor with a live visual
designer for [Cursorial](https://github.com/mstrobel/Cursorial), a .NET terminal UI framework
whose XAML renders to a character-cell grid rather than pixels.

## The core bet: no terminal emulator

A naive previewer would run the app against a PTY and re-interpret its VT byte stream — which
means writing a terminal emulator inside the IDE. We don't. Cursorial's `FrameRenderer` is
strictly the *last* pipeline stage ("composited `CellBuffer` → minimal VT bytes"), and the
framework already exposes the stage before it: `Cursorial.UI.Hosting.Headless.UIHeadlessHost` hosts the full
real pipeline (layout, styling, binding, themes, input routing, animation on a frozen clock)
headlessly against a `SyntheticTerminalHost`, exposing the composited screen as a `CellBuffer` of
plain `Cell(Grapheme, Kind, Style)` records.

The preview host reads that buffer directly and ships it to the IDE as structured runs
(`docs/protocol.md`); the plugin paints a monospace grid. No escape parsing, no scrollback, no
emulator — and because the preview runs the *real* `XamlLoader` and `Cursorial.UI`, it cannot
diverge from production rendering (the divergence concern that killed the earlier in-framework
previewer proposal — see Cursorial's `docs/ui-layer-design/proposal-xaml-source-gen.md` §3.7 and
the judgment docs).

## Process model

```
┌─────────────── Rider ────────────────┐      ┌────── PreviewHost (net10.0) ──────┐
│ Kotlin plugin (frontend-only)        │      │ StdioServer (main = UI thread)    │
│  · split editor: text + preview      │ stdin│  · PreviewSession                 │
│  · CellGridPanel paints frames       │─────▶│     · UIHeadlessHost (headless)       │
│  · sends resize/pointer/key/reload   │stdout│     · XamlLoader (CollectAll)     │
│  · restarts host on crash/rebuild    │◀─────│     · hit-test / property grid    │
└──────────────────────────────────────┘      └───────────────────────────────────┘
```

- **Transport**: newline-delimited JSON over stdio (`docs/protocol.md`). stderr is free logging.
- **Threading**: the host's main thread runs the command loop and *is* the UI thread
  (`UIHeadlessHost` is thread-affine to its creator; every framework API the session touches —
  hit-test, property reads, theme setters — demands that thread). A background thread pumps
  stdin into a bounded queue so frame emission never blocks on a slow reader.
- **Determinism**: frames advance only when a command steps them; time is a `FakeTimeProvider`
  driven by `advanceTime`. An idle preview costs zero CPU.
- **Lifecycle**: the plugin spawns one host per preview surface and kills/respawns on user-project
  rebuild (assemblies are loaded with `Assembly.LoadFrom` and never unloaded — same pragmatic
  model as Avalonia's previewer).

## What renders

`loadXaml` parses with `XamlDiagnosticMode.CollectAll` (stable `CURxxxx` codes, 1-based
line/column — the same diagnostics Cursorial's Roslyn generator surfaces at build time), then
instantiates through the real loader and hosts the root inside a preview-chrome `Border` whose
`Background` is a dynamic reference to `ThemeKeys.ElevationDesktop` — panels have no background
fill of their own, and a designed root is not necessarily hosted in a `Window`, so the desktop
elevation gives every preview an honest themed backdrop (and re-skins on theme flips). The
container is designer chrome: hit tests never report it. Parse errors keep the previous content
on screen. User-assembly types resolve after `XamlSchemaContext.Default
.RegisterAssembly`; Cursorial's built-in controls resolve out of the box.

Design-time metadata: the front end (Cursorial `designer-time-metadata` branch) understands
`mc:Ignorable` and the `d:` namespace (Blend URI or `https://cursorial.dev/xaml/design`). The
root's `d:DesignWidth`/`d:DesignHeight` constrain the previewed root inside the desktop chrome,
and `d:DataContext="vm:SomeViewModel"` is constructed and applied so `{Binding}`s render design
data — with zero runtime cost, since the parser skips `d:` everywhere outside the designer.

Not carried in v1: buffer fragments (Kitty/Sixel/iTerm2 images) — the cell grid ships without
them; a later protocol rev can attach image payloads (the IDE can paint real pixels, which is
strictly better than any terminal).

## Designer interactions

- **Click-to-select**: the plugin sends `hitTest`; the session uses the framework's own public
  `InputDispatcher.HitTest` (window-topology- and z-order-aware) and answers with the element
  chain and absolute cell rects (`TranslateToScreen` + `Bounds`). The plugin draws selection
  overlays itself — no framework adorner layer needed.
- **Property grid**: `getProperties` reports `UIObject.GetSetProperties()` with effective values,
  the winning lane (`ValueSource.Kind`), and `StyleDiagnostics.Explain` provenance text.
- **Interactive preview**: pointer/key/text commands inject synthetic input through the same
  queue production input uses; the preview is a running app, not a screenshot.

## Phasing

1. ✅ **Preview engine** (this repo, done): protocol + PreviewHost + tests.
2. **Rider plugin v1** (`plugin/`): split editor, frame painting, reload-on-save, diagnostics in
   the editor.
3. **Selection & inspection UI**: overlays, property panel, jump-to-source (needs element→source
   spans stamped in Cursorial — a worktree-branch change there).
4. **Editing**: property writes (`SetValue`/`ClearValue` discipline), design-time data
   (`d:`/`mc:Ignorable` support in the Cursorial front end).
5. **Deep code insight**: completion/goto via LSP or a ReSharper backend plugin reusing
   `Cursorial.UI.Xaml.Frontend` + `RoslynXamlMetadata` (the netstandard2.0 seam built for this).

## Cursorial changes policy

This repo consumes the sibling checkout read-only via `ProjectReference`
(`$(CursorialRepoRoot)`, overridable). When the designer needs framework-side seams, the change
is made on a branch in a git worktree under the Cursorial repo (`.worktrees/`), never in the main
checkout.
