using System.Reflection;

using Cursorial.Designer.Protocol;
using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Testing;
using Cursorial.UI.Themes;
using Cursorial.UI.Xaml;

namespace Cursorial.Designer.PreviewHost;

/// <summary>
/// One designer preview: a headless <see cref="UITestHost"/> (the framework's own full-pipeline
/// harness — real layout, styling, binding, theming) driven by protocol commands. The thread that
/// constructs the session and calls <see cref="Execute"/> is the UI thread; the frame loop only
/// advances when a command steps it, so previews are deterministic and cost nothing while idle.
/// </summary>
internal sealed class PreviewSession : IDisposable
{
    private readonly Action<PreviewEvent> _emit;
    private readonly XamlLoader _loader = new(new XamlLoaderOptions
    {
        DiagnosticMode = XamlDiagnosticMode.CollectAll,
        TrackSourceInfo = true, // element→source navigation reads XamlSourceRegistry at hit-test time
    });
    private readonly HashSet<string> _registeredAssemblies = new(StringComparer.Ordinal);

    // Element identity for hit-test/property round-trips. Ids are handed out lazily as elements
    // surface through hit tests, and are invalidated wholesale by the next successful load.
    // UIElement does not override Equals, so the default comparer is reference equality.
    private readonly List<UIElement> _elementsById = [];
    private readonly Dictionary<UIElement, int> _idsByElement = [];

    // Wire key names currently held down (kind:"down" without a matching "up"). A second down
    // for a held key is a keyboard auto-repeat, which the framework models as IsRepeat.
    private readonly HashSet<string> _heldKeys = new(StringComparer.OrdinalIgnoreCase);

    private UITestHost? _host;
    private StyleQuantizer? _quantizer;

    // The last emitted frame's full content, for no-change suppression and row-level deltas.
    // Play-mode ticks and pointer moves over static content cost nothing on the wire.
    private FrameEvent? _lastFrame;

    // The URI of the currently loaded document — the reference point for ElementRef.InDocument
    // (an element span from this URI supports direct caret sync; a foreign span is template
    // content from another document).
    private Uri? _documentUri;

    // The preview chrome: loaded roots are hosted in a Border whose background is the theme's
    // desktop elevation, because panels have no background fill of their own and a designed root
    // is not necessarily hosted in a Window. Never surfaced through hit tests — the user didn't
    // author it.
    private Border? _container;

    // The most recent exception that escaped preview content during a frame. The dispatcher
    // handler marks these Handled so one broken user control cannot latch the application into
    // its fatal-rethrow path; LoadXaml additionally uses this to roll back to the previous
    // content when a new document breaks on its first settle.
    private Exception? _frameException;

    public PreviewSession(Action<PreviewEvent> emit) => _emit = emit;

    public void Execute(PreviewCommand command)
    {
        switch (command)
        {
            case InitializeCommand initialize:
                Initialize(initialize);
                break;
            case LoadXamlCommand load:
                LoadXaml(load);
                break;
            case ResizeCommand resize:
                Host(command).SendResize(resize.Columns, resize.Rows);
                SettleAndEmitFrame();
                break;
            case PointerCommand pointer:
                Pointer(pointer);
                break;
            case KeyCommand key:
                SendKey(key);
                break;
            case TextCommand text:
                Host(command).SendText(text.Text);
                SettleAndEmitFrame();
                break;
            case AdvanceTimeCommand advance:
                Host(command).AdvanceTime(TimeSpan.FromMilliseconds(advance.Milliseconds));
                EmitFrame();
                break;
            case HitTestCommand hitTest:
                HitTest(hitTest);
                break;
            case GetChildrenCommand children:
                GetChildren(children);
                break;
            case GetPropertiesCommand properties:
                GetProperties(properties);
                break;
            case SampleCellCommand sample:
                SampleCell(sample);
                break;
            case AnalyzeCommand analyze:
                Analyze(analyze);
                break;
            case CompleteCommand complete:
                RegisterAssemblies(complete.Assemblies, complete.Id);
                _emit(new CompletionsEvent
                {
                    ReplyTo = complete.Id,
                    Items = EditorServices.Complete(complete.Xaml, complete.Line, complete.Column),
                });
                break;
            case SetThemeCommand theme:
                ApplyTheme(Host(command).Application, theme.ThemeBase, theme.ColorTier);
                SettleAndEmitFrame();
                break;
            default:
                _emit(new ErrorEvent { ReplyTo = command.Id, Message = $"Unhandled command type '{command.GetType().Name}'." });
                break;
        }

        // Frame-time exceptions from preview content are marked Handled (the session must
        // survive), but the user still needs to know their content threw. LoadXaml consumes the
        // slot itself when it rolls back; anything left here came from this command's frames.
        if (_frameException is { } ex)
        {
            _frameException = null;
            _emit(new ErrorEvent
            {
                ReplyTo = command.Id,
                Message = "Preview content threw during a frame; the rendered state may be incomplete.",
                Detail = ex.ToString(),
            });
        }
    }

    // ───────────────────────────── lifecycle ─────────────────────────────

    private void Initialize(InitializeCommand command)
    {
        if (_host is not null)
        {
            _emit(new ErrorEvent { ReplyTo = command.Id, Message = "Session is already initialized." });
            return;
        }

        if (command.ProtocolVersion != PreviewProtocol.Version)
        {
            _emit(new ErrorEvent
            {
                ReplyTo = command.Id,
                Message = $"Protocol version mismatch: host speaks {PreviewProtocol.Version}, plugin sent {command.ProtocolVersion}.",
            });
            return;
        }

        if (!TryMapCapabilities(command.Capabilities, out var capabilities))
        {
            _emit(new ErrorEvent
            {
                ReplyTo = command.Id,
                Message = $"Unknown capability profile '{command.Capabilities}'; using 'kitty-truecolor'.",
            });
        }

        _host = UITestHost.Create(new UITestHostOptions
        {
            Capabilities = capabilities,
            InitialSize = new Size(command.Columns, command.Rows),
        });

        // Exceptions escaping preview content during a frame (a user control's MeasureOverride,
        // a converter, a template body) must not latch the application into its fatal-rethrow
        // path — the protocol promises the session survives errors.
        _host.Application.DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            _frameException = args.Exception;
        };

        // The wire carries what the profiled terminal could actually show, not the pre-quantization
        // intent — an ansi16 preview must look like ansi16.
        _quantizer = new StyleQuantizer(_host.Application.Capabilities.Output);

        // The first-party control suites beyond the seeded core (Bars, Dialogs) are deliberately
        // not auto-discovered by the schema context; the previewer registers them so every
        // shipped control is XAML-addressable out of the box. KeyTips ride the Alt gate the
        // plugin already forwards.
        XamlSchemaContext.Default.RegisterAssembly(typeof(Toolbar).Assembly);
        XamlSchemaContext.Default.RegisterAssembly(typeof(Cursorial.UI.Dialogs.MessageBox).Assembly);
        _host.Application.EnableKeyTips();

        ApplyTheme(_host.Application, command.ThemeBase, command.ColorTier);

        _container = new Border();
        _container.SetResourceReference(Border.BackgroundProperty, ThemeKeys.ElevationDesktop);
        _host.ShowRoot(_container);

        SettleAndEmitFrame();
    }

    private UITestHost Host(PreviewCommand command)
        => _host ?? throw new InvalidOperationException($"'{command.GetType().Name}' arrived before 'initialize'.");

    public void Dispose() => _host?.Dispose();

    // ───────────────────────────── XAML ─────────────────────────────

    private void LoadXaml(LoadXamlCommand command)
    {
        var host = Host(command);
        RegisterAssemblies(command.Assemblies, command.Id);

        var sourceUri = command.SourceUri is { } s && Uri.TryCreate(s, UriKind.Absolute, out var parsed) ? parsed : null;
        var document = _loader.Parse(command.Xaml, sourceUri);

        var diagnostics = ToDiagnosticInfos(document.Diagnostics);

        if (diagnostics.Any(d => d.Severity == "error"))
        {
            // Parse/resolution errors: report and keep the previous content on screen.
            _emit(new DiagnosticsEvent { ReplyTo = command.Id, SourceUri = command.SourceUri, Items = diagnostics });
            return;
        }

        object root;
        try
        {
            root = _loader.Load(document, sourceUri is null ? null : new XamlLoadContext { Source = sourceUri });
        }
        catch (XamlParseException ex)
        {
            foreach (var d in ex.Diagnostics)
            {
                diagnostics.Add(new DiagnosticInfo
                {
                    Code = d.Code,
                    Message = d.Message,
                    Line = d.Line,
                    Column = d.Column,
                    Severity = "error",
                });
            }

            _emit(new DiagnosticsEvent { ReplyTo = command.Id, SourceUri = command.SourceUri, Items = diagnostics });
            return;
        }
        catch (Exception ex)
        {
            // Instantiation runs user code (constructors, static initializers, collection adds),
            // which can throw anything. The diagnostics contract still holds: report the collected
            // parse warnings plus a synthesized positioned error, and keep the previous content.
            diagnostics.Add(new DiagnosticInfo
            {
                Code = "CUR3000",
                Message = $"Instantiation failed: {ex.Message}",
                Line = 1,
                Column = 1,
                Severity = "error",
            });

            _emit(new DiagnosticsEvent { ReplyTo = command.Id, SourceUri = command.SourceUri, Items = diagnostics });
            _emit(new ErrorEvent { ReplyTo = command.Id, Message = "The document's content threw while being instantiated.", Detail = ex.ToString() });
            return;
        }

        if (root is not UIElement element)
        {
            _emit(new DiagnosticsEvent { ReplyTo = command.Id, SourceUri = command.SourceUri, Items = diagnostics });
            _emit(new ErrorEvent
            {
                ReplyTo = command.Id,
                Message = $"The document root is {root.GetType().Name}; the preview needs a UIElement root.",
            });
            return;
        }

        if (element is Window window)
        {
            // A Window shown outside the WindowManager never activates, and the theme styles
            // inactive windows dimmed (Opacity 0.70) behind a 2.5 s transition. Neutralize both —
            // in the designer, the document being edited is by definition the active one.
            Transition.SetTransitions(window, null);
            window.SetValue(UIElement.OpacityProperty, 1.0);
        }

        ApplyDesignInfo(document.DesignInfo, element);

        _elementsById.Clear();
        _idsByElement.Clear();
        _documentUri = sourceUri;

        var previous = _container!.Child;
        _container.Child = element;

        _emit(new DiagnosticsEvent { ReplyTo = command.Id, SourceUri = command.SourceUri, Items = diagnostics });

        // A document can load fine and still break on its first layout/render (a user control's
        // MeasureOverride, a template expanding). Roll back so the previous content really does
        // keep rendering, as the protocol promises.
        _frameException = null;
        SettleAndEmitFrame();
        if (_frameException is { } broken)
        {
            _container.Child = previous;
            _elementsById.Clear();
            _idsByElement.Clear();
            _frameException = null;
            SettleAndEmitFrame();
            _emit(new ErrorEvent
            {
                ReplyTo = command.Id,
                Message = "The document threw during layout/render; reverted to the previous content.",
                Detail = broken.ToString(),
            });
        }
    }

    /// <summary>
    /// Applies the document's design-time metadata: <c>d:DesignWidth</c>/<c>d:DesignHeight</c>
    /// constrain the root (the desktop chrome shows around it), and <c>d:DataContext</c> is
    /// constructed and set so <c>{Binding}</c>s render against design data.
    /// </summary>
    private void ApplyDesignInfo(XamlDesignInfo? design, UIElement element)
    {
        if (design is null)
            return;

        if (design.DesignWidth is { } width)
            element.SetValue(UIElement.WidthProperty, (int?)width);

        if (design.DesignHeight is { } height)
            element.SetValue(UIElement.HeightProperty, (int?)height);

        if (design.DataContextType is { Activate: { } activate })
        {
            try
            {
                element.SetValue(UIElement.DataContextProperty, activate());
            }
            catch (Exception ex)
            {
                _emit(new ErrorEvent
                {
                    Message = $"The d:DataContext type '{design.DataContextType.ClrType.Name}' threw during construction.",
                    Detail = ex.ToString(),
                });
            }
        }
    }

    private void RegisterAssemblies(IReadOnlyList<string>? assemblies, long? replyTo)
    {
        if (assemblies is null)
            return;

        foreach (var path in assemblies)
        {
            if (!_registeredAssemblies.Add(path))
                continue;

            try
            {
                XamlSchemaContext.Default.RegisterAssembly(Assembly.LoadFrom(path));
            }
            catch (Exception ex)
            {
                // Advisory only, and NEVER with the command's reply id: the real reply follows on
                // the same id, and an error here would satisfy the IDE's pending request first —
                // dropping the actual completions/diagnostics. Removing from the set retries the
                // load next request (a dll mid-rewrite during a build self-heals).
                _registeredAssemblies.Remove(path);
                _emit(new LogEvent { Level = "warn", Message = $"Failed to load assembly '{path}': {ex.Message}" });
            }
        }
    }

    /// <summary>
    /// Editor service: parse-only diagnostics for a (possibly mid-edit) document. No preview
    /// session required — a language-service host never initializes one.
    /// </summary>
    private void Analyze(AnalyzeCommand command)
    {
        RegisterAssemblies(command.Assemblies, command.Id);
        var uri = command.SourceUri is { } s && Uri.TryCreate(s, UriKind.Absolute, out var parsed) ? parsed : null;
        var document = _loader.Parse(command.Xaml, uri);
        _emit(new DiagnosticsEvent { ReplyTo = command.Id, SourceUri = command.SourceUri, Items = ToDiagnosticInfos(document.Diagnostics) });
    }

    private static List<DiagnosticInfo> ToDiagnosticInfos(IReadOnlyList<XamlDiagnostic> diagnostics)
        => diagnostics
            .Select(d => new DiagnosticInfo
            {
                Code = d.Code,
                Message = d.Message,
                Line = d.Line,
                Column = d.Column,
                Severity = d.Severity switch
                {
                    XamlDiagnosticSeverity.Error => "error",
                    XamlDiagnosticSeverity.Warning => "warning",
                    _ => "info",
                },
            })
            .ToList();

    // ───────────────────────────── input ─────────────────────────────

    private void Pointer(PointerCommand command)
    {
        var host = Host(command);
        var position = new CellPosition(command.Column, command.Row);
        if (!InputMapper.TryMapButton(command.Button, out var button))
        {
            _emit(new ErrorEvent { ReplyTo = command.Id, Message = $"Unknown mouse button '{command.Button}'." });
            return;
        }

        switch (command.Kind)
        {
            case "move":
                host.SendMouseMove(command.Column, command.Row);
                break;
            case "down" or "up":
                host.SendInput(new MouseEvent
                {
                    Kind = command.Kind == "down" ? MouseEventKind.ButtonDown : MouseEventKind.ButtonUp,
                    Position = position,
                    Button = button,
                    ButtonsHeld = MouseButtons.None,
                    Modifiers = KeyModifiers.None,
                    Timestamp = default, // SendInput stamps default timestamps on the fake clock
                });
                break;
            default:
                _emit(new ErrorEvent { ReplyTo = command.Id, Message = $"Unknown pointer kind '{command.Kind}'." });
                return;
        }

        SettleAndEmitFrame();
    }

    private void SendKey(KeyCommand command)
    {
        var host = Host(command);
        if (!InputMapper.TryMapKey(command.Key, out var key, out var text))
        {
            _emit(new ErrorEvent { ReplyTo = command.Id, Message = $"Unknown key '{command.Key}'." });
            return;
        }

        if (!InputMapper.TryMapModifiers(command.Modifiers, out var modifiers, out var unknown))
        {
            _emit(new ErrorEvent { ReplyTo = command.Id, Message = $"Unknown key modifier '{unknown}'." });
            return;
        }

        switch (command.Kind)
        {
            case null or "press":
                host.SendKey(key, modifiers, text, withRelease: true);
                break;
            case "down":
                host.SendInput(new KeyEvent
                {
                    Key = key,
                    Modifiers = modifiers,
                    Kind = KeyEventKind.Down,
                    IsRepeat = !_heldKeys.Add(command.Key),
                    Text = (text ?? string.Empty).AsMemory(),
                    Timestamp = default, // SendInput stamps default timestamps on the fake clock
                });
                break;
            case "up":
                _heldKeys.Remove(command.Key);
                host.SendInput(new KeyEvent
                {
                    Key = key,
                    Modifiers = modifiers,
                    Kind = KeyEventKind.Up,
                    Text = (text ?? string.Empty).AsMemory(),
                    Timestamp = default,
                });
                break;
            default:
                _emit(new ErrorEvent { ReplyTo = command.Id, Message = $"Unknown key kind '{command.Kind}' (expected 'down', 'up', or omitted)." });
                return;
        }

        SettleAndEmitFrame();
    }

    // ───────────────────────────── inspection ─────────────────────────────

    private void HitTest(HitTestCommand command)
    {
        var host = Host(command);
        var hit = host.Application.InputDispatcher.HitTest(new CellPosition(command.Column, command.Row));

        var elements = new List<ElementRef>();
        for (var element = hit; element is not null && !ReferenceEquals(element, _container); element = element.VisualParent)
            elements.Add(MakeElementRef(element));

        _emit(new HitTestResultEvent { ReplyTo = command.Id, Elements = elements });
    }

    private void GetChildren(GetChildrenCommand command)
    {
        _ = Host(command);
        if (command.ElementId < 0 || command.ElementId >= _elementsById.Count)
        {
            _emit(new ErrorEvent { ReplyTo = command.Id, Message = $"Unknown element id {command.ElementId} (stale after reload?)." });
            return;
        }

        var parent = _elementsById[command.ElementId];
        var elements = new List<ElementRef>(parent.VisualChildrenCount);
        for (var i = 0; i < parent.VisualChildrenCount; i++)
            elements.Add(MakeElementRef(parent.GetVisualChild(i)));

        _emit(new ChildrenEvent { ReplyTo = command.Id, ParentId = command.ElementId, Elements = elements });
    }

    private void SampleCell(SampleCellCommand command)
    {
        var host = Host(command);
        var manager = host.Application.WindowManager;
        if (manager is null)
        {
            _emit(new ErrorEvent { ReplyTo = command.Id, Message = "The window manager is not composed yet." });
            return;
        }

        var layers = manager.SampleCell(command.Column, command.Row)
            .Select(sample => new LayerSampleInfo
            {
                SurfaceZ = sample.SurfaceZ,
                Element = sample.ElementDescription,
                Grapheme = sample.Cell?.Grapheme,
                Kind = sample.Cell?.Kind.ToString(),
                Parameters = new CompositeParametersInfo
                {
                    OffsetColumn = sample.Parameters.OffsetColumn,
                    OffsetRow = sample.Parameters.OffsetRow,
                    Opacity = sample.Parameters.Opacity,
                    Clip = sample.Parameters.Clip?.ToString(),
                    Mode = sample.Parameters.Mode?.ToString(),
                },
                // The layer's carried style is the pre-quantization intent — exactly what the
                // composition inspector wants to see.
                Style = sample.Cell is { } cell ? FrameSerializer.ToStyleInfo(cell.Style) : null,
            })
            .ToList();

        _emit(new CellSamplesEvent { ReplyTo = command.Id, Column = command.Column, Row = command.Row, Layers = layers });
    }

    private ElementRef MakeElementRef(UIElement element)
    {
        var (column, row) = element.TranslateToScreen(0, 0);
        var source = XamlSourceRegistry.TryGetSourceInfo(element);
        return new ElementRef
        {
            ElementId = IdFor(element),
            ElementType = element.GetType().Name,
            Name = element.Name,
            Bounds = new CellRectInfo
            {
                Column = column,
                Row = row,
                Columns = element.Bounds.Columns,
                Rows = element.Bounds.Rows,
            },
            SourceUri = source?.Source?.ToString(),
            Line = source?.Line,
            Column = source?.Column,
            InDocument = source is null ? null : source.Source is not null && source.Source == _documentUri,
        };
    }

    private void GetProperties(GetPropertiesCommand command)
    {
        _ = Host(command);
        if (command.ElementId < 0 || command.ElementId >= _elementsById.Count)
        {
            _emit(new ErrorEvent { ReplyTo = command.Id, Message = $"Unknown element id {command.ElementId} (stale after reload?)." });
            return;
        }

        var element = _elementsById[command.ElementId];
        var items = new List<PropertyEntry>();
        foreach (var property in element.GetSetProperties())
        {
            // Provenance is best-effort decoration; a property the diagnostics can't explain
            // still belongs in the grid.
            string? explanation = null;
            IReadOnlyList<StyleFrameInfo>? frames = null;
            string? resourceKey = null;
            string? bindingTarget = null;
            IReadOnlyList<BindingExpressionInfo>? bindings = null;
            try
            {
                explanation = StyleDiagnostics.Explain(element, property);
                resourceKey = ResourceDiagnostics.GetResourceKey(element, property)?.ToString();

                var binding = Cursorial.UI.Data.BindingDiagnostics.Explain(element, property);
                if (binding.HasBindings)
                {
                    bindingTarget = binding.TargetDescription;
                    bindings = binding.Expressions.Select(expression => new BindingExpressionInfo
                    {
                        Lane = expression.Lane.ToString(),
                        Path = expression.Path,
                        Status = expression.Status.ToString(),
                        EffectiveMode = expression.EffectiveMode.ToString(),
                        ResolvedSourceChain = expression.ResolvedSourceChain,
                        Value = ValueFormatter.Format(expression.LastProducedValue),
                        LastFailure = expression.LastFailure == Cursorial.UI.Data.BindingFailureKind.None ? null : expression.LastFailure.ToString(),
                    }).ToList();
                }

                var details = StyleDiagnostics.ExplainDetails(element, property);
                if (details.HasFrames)
                {
                    frames = details.Frames.Select(frame => new StyleFrameInfo
                    {
                        Layer = frame.Layer.ToString(),
                        Selector = frame.SelectorDescription,
                        IsActive = frame.IsActive,
                        HasValue = frame.HasValue,
                        Value = ValueFormatter.Format(frame.LastProducedValue),
                        Swatch = ValueFormatter.SwatchHex(frame.LastProducedValue),
                        Status = frame.Status,
                        ResourceKey = frame.ResourceKey?.ToString(),
                        SortKey = frame.SortKey.ToString(),
                    }).ToList();
                }
            }
            catch
            {
            }

            var source = element.GetValueSource(property);
            items.Add(new PropertyEntry
            {
                Name = property.Name,
                Value = ValueFormatter.Format(element.GetValue(property)),
                Swatch = ValueFormatter.SwatchHex(element.GetValue(property)),
                ValueSource = source.Kind.ToString(),
                DeclaringType = property.OwnerType.IsInstanceOfType(element) ? null : property.OwnerType.Name,
                Explanation = explanation,
                Priority = source.Priority.ToString(),
                BasePriority = source.BasePriority != source.Priority ? source.BasePriority.ToString() : null,
                IsAnimated = source.IsAnimated ? true : null,
                ResourceKey = resourceKey,
                Frames = frames,
                BindingTarget = bindingTarget,
                Bindings = bindings,
            });
        }

        var classes = string.Join(", ", element.Classes);
        _emit(new PropertiesEvent
        {
            ReplyTo = command.Id,
            ElementId = command.ElementId,
            Classes = classes.Length == 0 ? null : classes,
            Items = items,
        });
    }

    private int IdFor(UIElement element)
    {
        if (_idsByElement.TryGetValue(element, out var id))
            return id;

        id = _elementsById.Count;
        _elementsById.Add(element);
        _idsByElement.Add(element, id);
        return id;
    }

    // ───────────────────────────── configuration ─────────────────────────────

    private static bool TryMapCapabilities(string? name, out Cursorial.Terminal.TerminalCapabilities capabilities)
    {
        (var known, capabilities) = name?.ToLowerInvariant() switch
        {
            null or "kitty-truecolor" => (true, TestCapabilities.KittyTruecolor),
            "ansi16" => (true, TestCapabilities.Ansi16Legacy),
            "no-motion" => (true, TestCapabilities.NoMotion),
            "kitty-graphics" => (true, TestCapabilities.KittyGraphics),
            "sixel" => (true, TestCapabilities.SixelGraphics),
            "iterm2" => (true, TestCapabilities.ITerm2Graphics),
            _ => (false, TestCapabilities.KittyTruecolor),
        };
        return known;
    }

    private void ApplyTheme(UIApplication application, string? themeBase, string? colorTier)
    {
        switch (themeBase?.ToLowerInvariant())
        {
            case "dark":
                application.RequestedThemeBase = ThemeBase.Dark;
                break;
            case "light":
                application.RequestedThemeBase = ThemeBase.Light;
                break;
            case null:
                break;
            default:
                _emit(new ErrorEvent { Message = $"Unknown theme base '{themeBase}' (expected 'dark' or 'light')." });
                break;
        }

        switch (colorTier?.ToLowerInvariant())
        {
            case "truecolor":
                application.RequestedColorTier = ColorDepth.Truecolor;
                break;
            case "ansi256":
                application.RequestedColorTier = ColorDepth.Ansi256;
                break;
            case "ansi16":
                application.RequestedColorTier = ColorDepth.Ansi16;
                break;
            case "nocolor":
                application.RequestedColorTier = ColorDepth.NoColor;
                break;
            case null:
                break;
            default:
                _emit(new ErrorEvent { Message = $"Unknown color tier '{colorTier}'." });
                break;
        }
    }

    // ───────────────────────────── frames ─────────────────────────────

    private void SettleAndEmitFrame()
    {
        if (_host is not { } host)
            return;

        Settle(host);
        EmitFrame();
    }

    private static void Settle(UITestHost host)
    {
        // Fast path: most content goes idle within a few frames.
        if (host.RunUntilIdle(maxFrames: 20))
            return;

        // Something is waiting on the frozen clock — a tooltip/repeat timer, a theme transition.
        // Advance virtual time in frame-interval steps (bounded at ~5 s virtual) so the preview
        // shows the settled end state instead of freezing at t=0 while burning frame passes.
        for (var i = 0; i < 150; i++)
        {
            host.AdvanceTime(host.Options.FrameInterval);
            if (host.RunUntilIdle(maxFrames: 5))
                return;
        }

        // Still animating (e.g. an indeterminate progress bar): emit as-is; advanceTime steps it.
    }

    private void EmitFrame()
    {
        if (_host is not { } host)
            return;

        var frame = FrameSerializer.Serialize(host.FrameBuffer, _quantizer);
        var delta = FrameSerializer.MakeDelta(_lastFrame, frame);
        _lastFrame = frame;
        if (delta is not null)
            _emit(delta);
    }
}
