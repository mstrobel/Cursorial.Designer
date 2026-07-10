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
{"type":"pointer","kind":"move|down|up","column":5,"row":2,"button":"left|right|middle"}
{"type":"key","key":"Enter","modifiers":["ctrl","alt","shift"],
 "kind":"down|up"}                         // omitted = complete press (down + up);
                                           // down/up are real transitions — holding a key holds
                                           // pressed state; repeated down while held = key repeat
{"type":"text","text":"abc"}
{"type":"advanceTime","milliseconds":100}  // drives the frozen clock (animations)
{"type":"hitTest","id":7,"column":5,"row":2}
{"type":"getProperties","id":8,"elementId":3}
{"type":"setTheme","themeBase":"light","colorTier":"truecolor"}
{"type":"shutdown"}
```

Key names: a single printable character, or `Enter`, `Tab`, `Escape`, `Up`, `Down`, `Left`,
`Right`, `Backspace`, `Delete`, `Home`, `End`, `PageUp`, `PageDown`, `F1`–`F12`, `Space`.

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

{"type":"properties","replyTo":8,"elementId":3,"items":[
  {"name":"Text","value":"Hello","valueSource":"Local","explanation":"…"}]}

{"type":"error","replyTo":null,"message":"…","detail":"…"}
{"type":"log","level":"debug|info|warn|error","message":"…"}
```

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
