using System.Reflection;

using Cursorial.Designer.Protocol;
using Cursorial.Input;
using Cursorial.Input.Events;
using Cursorial.Output;
using Cursorial.Rendering;
using Cursorial.UI;
using Cursorial.UI.Bars;
using Cursorial.UI.Controls;
using Cursorial.UI.Hosting.Headless;
using Cursorial.UI.Themes;
using Cursorial.UI.Xaml;

namespace Cursorial.Designer.PreviewHost;

/// <summary>
/// One designer preview: a headless <see cref="UIHeadlessHost"/> (the framework's own full-pipeline
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

    private UIHeadlessHost? _host;
    private StyleQuantizer? _quantizer;

    // The last emitted frame's full content, for no-change suppression and row-level deltas.
    // Play-mode ticks and pointer moves over static content cost nothing on the wire.
    private FrameEvent? _lastFrame;

    // The URI of the currently loaded document — the reference point for ElementRef.InDocument
    // (an element span from this URI supports direct caret sync; a foreign span is template
    // content from another document).
    private Uri? _documentUri;

    // A document whose root IS a Window shows through the WindowManager (windows are not
    // parentable children: hosted in the backdrop Border they render scattered chrome, no
    // frame, and dead window controls). Tracked so reloads close the previous one.
    private Window? _shownWindow;

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

    static PreviewSession()
    {
        // Design surfaces edit documents ON DISK: a relative <ResourceDictionary Source="…"/>
        // resolves against the document's file:// URI, so the previewer's provider must read
        // files — falling back to the standard embedded-resource lookup (cursorial://, which the
        // user's built assemblies answer once registered). Process-global, host-owned process.
        Cursorial.UI.Xaml.XamlModule.ResourceProvider = DesignerResources;
    }

    /// <summary>The previewer's resource provider — also the per-load dependency recorder.</summary>
    private static readonly DesignerXamlResourceProvider DesignerResources = new();

    public PreviewSession(Action<PreviewEvent> emit) => _emit = emit;

    private sealed class DesignerXamlResourceProvider : Cursorial.UI.Xaml.IXamlResourceProvider
    {
        private readonly Cursorial.UI.Xaml.EmbeddedXamlResourceProvider _embedded = new();

        /// <summary>The current document's directory — the probe root for scheme URIs.</summary>
        public string? DocumentDirectory { get; set; }

        /// <summary>Every file the current load consumed — the IDE's watch list.</summary>
        public List<string> ReadFiles { get; } = [];

        public bool TryGetXaml(Uri uri, out string? xaml)
        {
            xaml = null;
            if (!uri.IsAbsoluteUri)
                return false;

            if (uri.IsFile)
                return TryReadFile(uri.LocalPath, out xaml);

            // Live-over-baked: a cursorial://-style reference whose path exists in the project
            // (probed from the document's ancestors, the same convention navigation uses) reads
            // the FILE — the designer previews what the user is editing, not what the last
            // build embedded. Misses fall through to the embedded lookup.
            if (DocumentDirectory is { } root
                && (string.Equals(uri.Scheme, "cursorial", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "embedded", StringComparison.OrdinalIgnoreCase)))
            {
                var relative = uri.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                for (var dir = root; dir is { Length: > 0 }; dir = Path.GetDirectoryName(dir))
                {
                    if (TryReadFile(Path.Combine(dir, relative), out xaml))
                        return true;
                }
            }

            return _embedded.TryGetXaml(uri, out xaml);
        }

        private bool TryReadFile(string path, out string? xaml)
        {
            xaml = null;
            if (!File.Exists(path))
                return false;

            xaml = File.ReadAllText(path);
            ReadFiles.Add(Path.GetFullPath(path));
            return true;
        }
    }

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
            case PointerCommand or KeyCommand or TextCommand or AdvanceTimeCommand or ResizeCommand when _host is null:
                // Input racing a (re)initialize: transient by nature — the user clicked while the
                // host was restarting. Dropping it beats tearing down the fresh session.
                _emit(new LogEvent { Level = "debug", Message = $"Dropped '{command.GetType().Name}' that arrived before 'initialize'." });
                break;
            case HitTestCommand or DescribeElementCommand or GetChildrenCommand or GetPropertiesCommand or SampleCellCommand when _host is null:
                // Queries racing a (re)initialize answer with a reply-bearing error so the
                // plugin's pending correlation resolves instead of waiting out its timeout.
                _emit(new ErrorEvent { ReplyTo = command.Id, Message = $"'{command.GetType().Name}' arrived before 'initialize' (host restarting?)." });
                break;
            case ResizeCommand resize:
            {
                // Never squish the surface below the loaded root's declared minimum — the frame
                // reports its actual size and the IDE scrolls the overflow instead of clipping.
                var root = _container?.Child;
                var columns = Math.Max(resize.Columns, root?.GetValue(UIElement.MinWidthProperty) ?? 0);
                var rows = Math.Max(resize.Rows, root?.GetValue(UIElement.MinHeightProperty) ?? 0);
                Host(command).SendResize(columns, rows);
                SettleAndEmitFrame();
                break;
            }
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
            case DescribeElementCommand describe:
                DescribeElement(describe);
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
            case HoverCommand hover:
                Hover(hover);
                break;
            case DefinitionCommand definition:
                Definition(definition);
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

        _host = UIHeadlessHost.Create(new UIHeadlessHostOptions
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

    private UIHeadlessHost Host(PreviewCommand command)
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

        DesignerResources.DocumentDirectory =
            sourceUri is { IsFile: true } ? Path.GetDirectoryName(sourceUri.LocalPath) : null;
        DesignerResources.ReadFiles.Clear();

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

            EmitDependencies(command.Id);
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

            EmitDependencies(command.Id);
            _emit(new DiagnosticsEvent { ReplyTo = command.Id, SourceUri = command.SourceUri, Items = diagnostics });
            _emit(new ErrorEvent { ReplyTo = command.Id, Message = "The document's content threw while being instantiated.", Detail = ex.ToString() });
            return;
        }

        EmitDependencies(command.Id);

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

        var previousChild = _container!.Child;
        var previousWindow = _shownWindow;
        _container.Child = null;
        if (previousWindow is not null)
        {
            previousWindow.Close();
            _shownWindow = null;
        }

        AttachRoot(element);

        _emit(new DiagnosticsEvent { ReplyTo = command.Id, SourceUri = command.SourceUri, Items = diagnostics });

        // A document can load fine and still break on its first layout/render (a user control's
        // MeasureOverride, a template expanding). Roll back so the previous content really does
        // keep rendering, as the protocol promises.
        _frameException = null;
        SettleAndEmitFrame();
        if (_frameException is { } broken)
        {
            if (_shownWindow is { } shown)
            {
                shown.Close();
                _shownWindow = null;
            }

            _container.Child = null;
            if (previousWindow is not null)
                AttachRoot(previousWindow);
            else
                _container.Child = previousChild;

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
    /// Attaches a document root to the preview: Windows show through the WindowManager (the
    /// only path that gives them placement, frame chrome, and working window controls); every
    /// other element is hosted in the backdrop container.
    /// </summary>
    private void AttachRoot(UIElement element)
    {
        if (element is Window window)
        {
            window.Show();
            _shownWindow = window;
        }
        else
        {
            _container!.Child = element;
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
        var items = ToDiagnosticInfos(document.Diagnostics);

        // Build-phase diagnostics (CUR2105 no-collection-access, unresolvable x:Reference, setter
        // failures): the graph builder raises these at REALIZATION, so a parse-only pass never
        // sees them. Attempt the build when the parse is clean — atop parse errors it would only
        // cascade noise. The throwaway tree runs user constructors, exactly what the previewer
        // does on every reload; anything non-XAML it throws is swallowed (fail open — the
        // previewer's own load channel reports instantiation failures).
        if (!items.Any(d => d.Severity == "error"))
        {
            try
            {
                _ = _loader.Load(document, uri is null ? null : new XamlLoadContext { Source = uri });
            }
            catch (XamlParseException ex)
            {
                foreach (var d in ex.Diagnostics)
                {
                    items.Add(new DiagnosticInfo
                    {
                        Code = d.Code,
                        Message = d.Message,
                        Line = d.Line,
                        Column = d.Column,
                        Severity = "error",
                    });
                }
            }
            catch
            {
            }
        }

        _emit(new DiagnosticsEvent
        {
            ReplyTo = command.Id,
            SourceUri = command.SourceUri,
            Items = items,
            Tokens = command.Classify == true ? EditorServices.ClassifyTokens(command.Xaml) : null,
        });
    }

    /// <summary>Editor service: symbol info at a position — signature, XML-doc summary, value detail.</summary>
    private void Hover(HoverCommand command)
    {
        RegisterAssemblies(command.Assemblies, command.Id);
        var symbol = EditorServices.SymbolAt(command.Xaml, command.Line, command.Column, command.FilePath);
        var summary = symbol is { Owner: { } owner, DocIds.Count: > 0 } ? EditorServices.XmlSummary(owner, symbol.DocIds) : null;
        _emit(new HoverInfoEvent
        {
            ReplyTo = command.Id,
            Signature = symbol?.Signature,
            Summary = summary,
            Detail = symbol?.Detail,
        });
    }

    /// <summary>Editor service: source location of the symbol at a position, via portable PDBs.</summary>
    private void Definition(DefinitionCommand command)
    {
        RegisterAssemblies(command.Assemblies, command.Id);
        var symbol = EditorServices.SymbolAt(command.Xaml, command.Line, command.Column, command.FilePath);
        // In-document targets (named elements, document resource keys) carry their own location;
        // everything else resolves through the assembly's portable PDB.
        var location = symbol?.Location
            ?? (symbol?.Owner is { } owner ? EditorServices.SourceLocationOf(owner, symbol.Member) : null);
        _emit(new DefinitionEvent
        {
            ReplyTo = command.Id,
            File = location?.File,
            Line = location?.Line,
            Column = location?.Column,
            Symbol = symbol?.Display,
        });
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

        // Terminal parity: a real terminal delivers plain space as Key.Character ' ' (the
        // printable path); Key.Space exists only for Ctrl+Space (the NUL byte). Sending the
        // named key here meant TextBox text input never saw spaces.
        if (key == Key.Space && !modifiers.HasFlag(KeyModifiers.Control))
            key = Key.Character;

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

    private void DescribeElement(DescribeElementCommand command)
    {
        _ = Host(command);
        if (command.ElementId < 0 || command.ElementId >= _elementsById.Count)
        {
            _emit(new ErrorEvent { ReplyTo = command.Id, Message = $"Unknown element id {command.ElementId} (stale after reload?)." });
            return;
        }

        var elements = new List<ElementRef>();
        for (var element = _elementsById[command.ElementId]; element is not null && !ReferenceEquals(element, _container); element = element.VisualParent)
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
                Style = sample.Cell is { } cell ? FrameSerializer.ToStyleInfo(cell.Style, LightBase(_host!)) : null,
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

    /// <summary>The files the load just consumed — the IDE's reload-watch list.</summary>
    private void EmitDependencies(long? replyTo)
        => _emit(new DependenciesEvent
        {
            ReplyTo = replyTo,
            Files = DesignerResources.ReadFiles.Distinct(StringComparer.Ordinal).ToList(),
        });

    /// <summary>
    /// The type qualification for a property row, or null when the name is addressable as a plain
    /// member of the element's own type — declared, inherited, or AddOwner'd (TextBlock.Foreground
    /// needs no "TextElement." prefix; Grid.Row on a Button keeps "Grid.").
    /// </summary>
    private static string? DeclaringTypeFor(UIElement element, UIProperty property)
    {
        if (property.OwnerType.IsInstanceOfType(element) || UIProperties.Find(element.GetType(), property.Name) == property)
            return null;
        return property.OwnerType.Name;
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

        // Layout facts lead the grid: not UIProperties (no lanes, no provenance) but the two
        // numbers every layout question starts from.
        items.Add(new PropertyEntry { Name = "DesiredSize", Value = ValueFormatter.Format(element.DesiredSize), ValueSource = "Layout" });
        items.Add(new PropertyEntry { Name = "Bounds", Value = ValueFormatter.Format(element.Bounds), ValueSource = "Layout" });

        var setProperties = element.GetSetProperties();
        foreach (var property in setProperties)
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
                DeclaringType = DeclaringTypeFor(element, property),
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

        // Inherited contributions have no store entry on this element — GetSetProperties
        // excludes them by design ("not SET here") — but they absolutely belong in the
        // inspector. The registry's inheriting set (UIProperties) is the list of properties
        // worth asking; GetValueSource narrows to the ones actually flowing from an ancestor.
        foreach (var property in UIProperties.Inheriting)
        {
            if (setProperties.Contains(property))
                continue;

            ValueSource source;
            try
            {
                source = element.GetValueSource(property);
            }
            catch
            {
                continue;
            }

            if (source.Kind != ValueSourceKind.Inherited)
                continue;

            string? explanation = null;
            try
            {
                explanation = StyleDiagnostics.Explain(element, property);
            }
            catch
            {
            }

            items.Add(new PropertyEntry
            {
                Name = property.Name,
                Value = ValueFormatter.Format(element.GetValue(property)),
                Swatch = ValueFormatter.SwatchHex(element.GetValue(property)),
                ValueSource = source.Kind.ToString(),
                DeclaringType = DeclaringTypeFor(element, property),
                Explanation = explanation,
                Priority = source.Priority.ToString(),
                BasePriority = source.BasePriority != source.Priority ? source.BasePriority.ToString() : null,
                IsAnimated = source.IsAnimated ? true : null,
            });
        }

        // Default-lane properties on demand: everything registered for the element's type
        // that no lane contributes to. The set/inherited loops above covered the rest, and the
        // Kind filter keeps the three passes disjoint by construction.
        if (command.IncludeDefaults == true)
        {
            foreach (var property in UIProperties.ForType(element.GetType()))
            {
                if (setProperties.Contains(property))
                    continue;

                ValueSource source;
                string? formatted;
                string? swatch;
                try
                {
                    source = element.GetValueSource(property);
                    if (source.Kind != ValueSourceKind.Default)
                        continue;
                    var value = element.GetValue(property);
                    formatted = ValueFormatter.Format(value);
                    swatch = ValueFormatter.SwatchHex(value);
                }
                catch
                {
                    continue;
                }

                items.Add(new PropertyEntry
                {
                    Name = property.Name,
                    Value = formatted,
                    Swatch = swatch,
                    ValueSource = source.Kind.ToString(),
                    DeclaringType = DeclaringTypeFor(element, property),
                    Priority = source.Priority.ToString(),
                    // Theme-reactive defaults resolve through a key — provenance worth showing.
                    ResourceKey = property.GetDefaultResourceKey(element.GetType())?.ToString(),
                });
            }
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
            null or "kitty-truecolor" => (true, HeadlessCapabilities.KittyTruecolor),
            "ansi16" => (true, HeadlessCapabilities.Ansi16Legacy),
            "no-motion" => (true, HeadlessCapabilities.NoMotion),
            "kitty-graphics" => (true, HeadlessCapabilities.KittyGraphics),
            "sixel" => (true, HeadlessCapabilities.SixelGraphics),
            "iterm2" => (true, HeadlessCapabilities.ITerm2Graphics),
            _ => (false, HeadlessCapabilities.KittyTruecolor),
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

    private static void Settle(UIHeadlessHost host)
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

    /// <summary>Whether ANSI palette indices should resolve through the light-base palette.</summary>
    private static bool LightBase(UIHeadlessHost host) => host.Application.RequestedThemeBase == ThemeBase.Light;

    private void EmitFrame()
    {
        if (_host is not { } host)
            return;

        var frame = FrameSerializer.Serialize(host.FrameBuffer, _quantizer, LightBase(host));
        var delta = FrameSerializer.MakeDelta(_lastFrame, frame);
        _lastFrame = frame;
        if (delta is not null)
            _emit(delta);
    }
}
