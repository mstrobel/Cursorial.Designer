# Preview protocol (v1)

The IDE plugin and the preview host speak **newline-delimited JSON** — UTF-8, one object per
line. Commands flow plugin → host on **stdin**; events flow host → plugin on **stdout**;
**stderr** is free-form logging (never parsed). Every message carries a `type` discriminator.
The C# shapes in `Cursorial.Designer.Protocol` are the normative definition; this document is
the cross-language summary the Kotlin side is written against.

Correlation: commands that expect a specific answer (`hitTest`, `getProperties`) may carry a
numeric `id`; the answering event echoes it as `replyTo`. Unsolicited events (frames pushed
after input, logs) have no `replyTo`.

Session shape: the host emits `ready` once at startup. The plugin must send `initialize` before
anything else. `loadXaml` is always answered by a `diagnostics` event (possibly empty) and, when
the document produced a showable root, a `frame`. Any command may be answered by `error` instead;
the session survives errors and keeps rendering the previous content.

## Commands (plugin → host)

```jsonc
{"type":"initialize","protocolVersion":1,"columns":80,"rows":24,
 "capabilities":"kitty-truecolor",     // optional synthetic terminal profile
 "themeBase":"dark","colorTier":null}  // optional theme selection
{"type":"loadXaml","xaml":"<StackPanel …>","sourceUri":"file:///…/MainView.xaml",
 "assemblies":["/path/UserApp.dll"]}   // user build output to register for type resolution
{"type":"resize","columns":100,"rows":30}
{"type":"pointer","kind":"move|down|up","column":5,"row":2,"button":"left|right|middle",
 "modifiers":["ctrl","alt","shift"]}       // ambient modifiers at pointer time (a terminal can't read
                                           // them), snapshotted by the previewer for Shift/Ctrl-click +
                                           // Shift-drag; omitted/empty = none
{"type":"key","key":"Enter","modifiers":["ctrl","alt","shift"],
 "kind":"down|up"}                         // omitted = complete press (down + up);
                                           // down/up are real transitions — holding a key holds
                                           // pressed state; repeated down while held = key repeat
{"type":"text","text":"abc"}
{"type":"advanceTime","milliseconds":100}  // drives the frozen clock (animations)
{"type":"hitTest","id":7,"column":5,"row":2}
{"type":"getChildren","id":9,"elementId":3}   // descend below a hit-test anchor / explore siblings
{"type":"describeElement","id":12,"elementId":3}  // re-answers hitTestResult with FRESH bounds
                                           // (selection refresh after resize/relayout)
{"type":"getProperties","id":8,"elementId":3}  // includeDefaults:true adds default-lane rows
                                           // loadXaml also answers a `dependencies` event listing
                                           // the on-disk files it consumed (linked dictionaries) —
                                           // the IDE watches them and reloads on change
{"type":"sampleCell","id":10,"column":5,"row":2}  // per-cell composition inspector
{"type":"analyze","id":11,"xaml":"<…>","sourceUri":"file:///…",
 "assemblies":["…"],"classify":true}       // editor service: parse-only diagnostics; valid
                                           // BEFORE initialize (language-service hosts never
                                           // initialize a preview session). classify:true adds
                                           // semantic tokens to the diagnostics reply
{"type":"complete","id":12,"xaml":"<…>","line":2,"column":10,
 "assemblies":["…"]}                       // editor service: completion at a 1-based position
{"type":"hover","id":13,"xaml":"<…>","line":2,"column":7,
 "assemblies":["…"],"filePath":"/…/View.xaml"}  // editor service: symbol signature + XML-doc
                                           // summary; filePath lets in-document targets (named
                                           // elements, document resource keys) report locations
{"type":"definition","id":14,"xaml":"<…>","line":2,"column":7,
 "assemblies":["…"],"filePath":"/…/View.xaml"}  // editor service: source location via portable
                                           // PDBs, or in-document for x:Reference/x:Key targets
{"type":"setTheme","themeBase":"light","colorTier":"truecolor"}
{"type":"shutdown"}
```

Key names: a single printable character, or `Enter`, `Tab`, `Escape`, `Up`, `Down`, `Left`,
`Right`, `Backspace`, `Delete`, `Insert`, `Home`, `End`, `PageUp`, `PageDown`, `F1`–`F12`,
`Space` — plus the modifier keys as real keys: `Alt`, `Ctrl`, `Shift`, `Meta`, `Super`/`Cmd`,
`AltGr`, and `Right`-prefixed variants (`RightAlt`, …). Modifier keys matter as down/up
transitions: the access-key display gates on Alt down (cue shown), latches on Alt up, and
exits on Escape — WPF-style.

## Events (host → plugin)

```jsonc
{"type":"ready","protocolVersion":1,"pid":1234}

{"type":"frame","columns":80,"rows":24,
 "cursor":{"row":0,"column":0,"visible":false,"shape":"default"},
 "styles":[{"fg":"#c0caf5","bg":"#1a1b26","attrs":["bold"]},{}],
 "lines":[[{"t":"Hello","s":0,"w":5},{"t":" ","s":1,"w":75}], …]}

{"type":"diagnostics","sourceUri":"file:///…","items":[
  {"code":"CUR2001","message":"…","line":3,"column":5,"severity":"error"}]}

{"type":"hitTestResult","replyTo":7,"elements":[            // innermost first
  {"elementId":3,"elementType":"Button","name":"ok",
   "bounds":{"column":2,"row":1,"columns":10,"rows":3}}]}

{"type":"children","replyTo":9,"parentId":3,"elements":[ /* same shape as hitTestResult */ ]}

{"type":"cellSamples","replyTo":10,"column":5,"row":2,"layers":[   // bottom → top
  {"surfaceZ":0,"element":"DockPanel","grapheme":"a","kind":"Single",
   "parameters":{"offsetColumn":0,"offsetRow":0,"opacity":255,"clip":"…","mode":null},
   "style":{"fg":"#c0caf5","bg":"#1a1b26"}}]}   // style = pre-quantization intent; null when
                                                // the cell is outside the layer's footprint

{"type":"properties","replyTo":8,"elementId":3,"items":[
  {"name":"Text","value":"Hello","valueSource":"Local","explanation":"…"}]}

// With classify:true, diagnostics also carries semantic token ranges (1-based; l=line,
// c=column, n=length). Kinds: element, attribute, attached, directive, extension, comment,
// string, brace, dot, parameter, resourceKey, bindingPath, elementRef, staticMember, number,
// enumValue, bool. Dotted names split (owner=element, '.'=dot, member=attribute/attached);
// extension arguments classify by role per extension; plain values classify by property type.
// "tokens":[{"l":2,"c":6,"n":9,"k":"element"}, …]

{"type":"hoverInfo","replyTo":13,
 "signature":"class Cursorial.UI.Controls.Button : ContentControl",
 "summary":"A themed, focusable push button …",  // from the assembly's XML doc file
 "detail":"\"Theme.ElevationDesktop\""}          // e.g. a constant's resolved value (x:Static)

{"type":"definition","replyTo":14,"file":"/…/Cursorial.UI/Controls/Button.cs",
 "line":12,"column":14,"symbol":"Button"}  // from portable PDB sequence points; the IDE
                                           // verifies the path exists locally

{"type":"completions","replyTo":12,"items":[
  {"text":"Button","kind":"element","detail":"Cursorial.UI.Controls"},
  {"text":"Content","kind":"attribute"},
  {"text":"Visible","kind":"value","detail":"Visibility"},
  {"text":"ThemeKeys.PanelBrush","kind":"value","detail":"Theme.PanelBrush",
   "insert":"{x:Static ThemeKeys.PanelBrush}"}]}  // insert: text to insert when it differs from
                                                  // the display/match text (additive field)

{"type":"error","replyTo":null,"message":"…","detail":"…"}
{"type":"log","level":"debug|info|warn|error","message":"…"}
```

### Delta frames and suppression

An unchanged frame is **not emitted at all** — play-mode `advanceTime` ticks and pointer moves
over static content cost nothing on the wire. When some rows changed and the dimensions did
not, the host emits a **row-level delta**: `"delta": true`, `lines` empty, and `changed` carrying
`{"i": rowIndex, "runs": […]}` entries whose style indices reference *this* event's `styles`
table. A full frame (no `delta` member) is sent on the first emission and whenever dimensions
change; it fully replaces the client's cached state.

### Frame encoding

Cell content is run-length encoded per row. `lines` has exactly `rows` entries (top to bottom);
each entry is the row's runs, left to right. A run is `{"t","s","w"}`: `t` the concatenated
grapheme clusters, `s` an index into the frame's `styles` table, `w` the number of cells covered.
Run widths always sum to `columns` for every row — wide graphemes cover two cells, so `w` can
exceed the cluster count of `t`. Blank cells arrive as runs of spaces. The style table is
deduplicated per frame; colors are `#RRGGBB`, an **omitted** color member means the viewer's
default fg/bg, and absent style members generally mean "not set". `attrs` may contain: bold,
dim, italic, underline, blink, reverse, hidden, strikethrough, overline. Styles are quantized to
the session's capability profile before encoding — an `ansi16` preview only ever carries the
16-color palette.

Element ids are stable for the lifetime of the currently loaded document and invalidated by the
next `loadXaml`.

## Versioning

`protocolVersion` is a single integer negotiated in `initialize`/`ready`. Additive changes
(new optional fields, new event types the plugin may ignore) do not bump it; breaking shape
changes do.
