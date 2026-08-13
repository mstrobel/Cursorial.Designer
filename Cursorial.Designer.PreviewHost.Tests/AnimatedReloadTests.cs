using System.Diagnostics;

using Cursorial.Animation;
using Cursorial.Designer.PreviewHost;
using Cursorial.Designer.Protocol;
using Cursorial.Media;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Media;

using Xunit.Abstractions;

namespace Cursorial.Designer.Tests.PreviewHost;

/// <summary>
/// The in-tests animated fixture: scaled text (figlet under the headless profiles — no OSC 66)
/// over a <see cref="PhaseShiftedBrush"/> whose <c>Phase</c> runs a Forever animation begun at
/// attach. <see cref="LastBrush"/> hands the test a reference to the most recently created brush
/// so a reload can probe what happens to the OLD tree's animation after detach. Deliberately does
/// NOT stop the animation on detach — mirroring a real document, where nothing stops a decorative
/// animation when the designer swaps trees (and a storyboard track cannot target a brush's
/// sub-object property from markup in the first place — the animation must be begun in code).
/// </summary>
public sealed class AnimatedShimmerProbe : Border
{
    public static PhaseShiftedBrush? LastBrush;

    private readonly PhaseShiftedBrush _brush = new(new LinearGradientBrush(
        Color.FromRgb(30, 30, 60), Color.FromRgb(220, 220, 255), spread: GradientSpread.Repeat));

    public AnimatedShimmerProbe()
    {
        LastBrush = _brush;
        Background = _brush;
        var text = new TextBlock { Text = "MARQUEE" };
        TextElement.SetSizing(text, TextSizing.Double);
        Child = text;
    }

    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(e);
        _brush.BeginAnimation(
            PhaseShiftedBrush.PhaseProperty,
            new RepeatAnimation<double>(
                new Animation<double>(0.0, 1.0, TimeSpan.FromSeconds(1), Interpolator.For<double>()),
                count: null)); // Forever — the marquee never idles
    }
}

/// <summary>
/// Regression facts for the reload-while-animating starvation, pinned with real motion via the
/// session's <c>keepAnimationsEnabled</c> test hook (production sessions run the design-surface
/// posture — animations disabled — which would mask the path under test). The captured defect: a
/// perpetually animated document streams play-mode frames fine, but a fresh <c>loadXaml</c> (the
/// editor reformatting attributes) ran the settle loop, which can never reach idle under a
/// Forever animation and burned its entire 920-frame budget — ~155 of them full render+diff
/// passes — before answering. The command loop IS the UI thread, so the wire starved for the
/// duration (6+ s at 300×100 in the live capture; ~34 s on the maintainer's document).
/// </summary>
public class AnimatedReloadTests : IDisposable
{
    private const string Xmlns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private const string ProbeNs =
        "xmlns:t=\"clr-namespace:Cursorial.Designer.Tests.PreviewHost;assembly=Cursorial.Designer.PreviewHost.Tests\"";

    private readonly List<PreviewEvent> _events = [];
    private readonly PreviewSession _session;
    private readonly ITestOutputHelper _output;

    public AnimatedReloadTests(ITestOutputHelper output)
    {
        _output = output;
        _session = new PreviewSession(_events.Add, keepAnimationsEnabled: true);
    }

    public void Dispose() => _session.Dispose();

    /// <summary>The animated document; <paramref name="separator"/> varies attribute formatting only.</summary>
    internal static string Document(string separator, int shimmers = 1)
    {
        var items = string.Join('\n', Enumerable.Repeat("    <t:AnimatedShimmerProbe/>", shimmers));
        return $"""
                <StackPanel {Xmlns} {ProbeNs}>
                {items}
                    <TextBlock{separator}Text="steady"/>
                </StackPanel>
                """;
    }

    private void Load(string xaml, long id) => _session.Execute(new LoadXamlCommand
    {
        Id = id,
        Xaml = xaml,
        Assemblies = [typeof(AnimatedShimmerProbe).Assembly.Location],
    });

    [Fact]
    public void Reload_while_animating_answers_promptly_and_streaming_stays_healthy()
    {
        // The surface size matters: the settle loop's cost is per-frame render+diff of the whole
        // buffer, so the pre-fix stall scales with area (355 ms at 80×24, ~2.6 s at this size,
        // 6+ s at 300×100). The budget-bounded settle answers in well under a second regardless.
        _session.Execute(new InitializeCommand { ProtocolVersion = 1, Columns = 240, Rows = 64 });
        Assert.IsType<FrameEvent>(_events.Last());

        Load(Document(" ", shimmers: 6), id: 1);
        Assert.Contains(_events, e => e is DiagnosticsEvent { ReplyTo: 1, Items.Count: 0 });
        Assert.DoesNotContain(_events, e => e is ErrorEvent);

        // The healthy half, pinned: a play-mode tick advances the marquee and emits a frame
        // WITHOUT settling (steady-state streaming is what the maintainer saw working).
        var beforeTick = _events.Count;
        _session.Execute(new AdvanceTimeCommand { Milliseconds = 250 });
        Assert.Contains(_events.Skip(beforeTick), e => e is FrameEvent);

        // The failure shape: a reformatted (value-identical) document arrives as a fresh load
        // while the animation runs. The settle loop can never reach idle — the reply must come
        // from the wall-clock-bounded settle, not from burning the full 150-iteration budget.
        var beforeReload = _events.Count;
        var stall = Stopwatch.StartNew();
        Load(Document("  ", shimmers: 6), id: 2);
        stall.Stop();

        _output.WriteLine($"Reload-while-animating executed in {stall.ElapsedMilliseconds} ms.");
        Assert.Contains(_events.Skip(beforeReload), e => e is DiagnosticsEvent { ReplyTo: 2, Items.Count: 0 });
        Assert.True(
            stall.ElapsedMilliseconds < 1300,
            $"Reload while animating took {stall.ElapsedMilliseconds} ms — the settle loop starved the command loop.");

        // Streaming stays healthy after the reload: the new tree's animation ticks frames. (The
        // reload itself may legitimately emit NO frame — a value-identical document can settle
        // to an identical screen, and an empty delta is suppressed — so the post-reload pin is
        // on the tick, whose quarter-period phase advance always changes cells.)
        var afterReload = _events.Count;
        _session.Execute(new AdvanceTimeCommand { Milliseconds = 250 });
        Assert.Contains(_events.Skip(afterReload), e => e is FrameEvent);
    }

    /// <summary>
    /// Assessment probe for the framework routing (not a defect this repo can fix): the
    /// scheduler's detach-stop (<c>AnimationScheduler.OnElementDetached</c>) retires instances
    /// whose <c>TargetObject</c> IS the detached element — an instance targeting a SUB-OBJECT
    /// (the brush held in a styled property of a detached element) is never retired. The old
    /// tree's Phase animation therefore keeps ticking after a reload: it holds
    /// <c>HasActiveAnimations</c> true forever and mutates the orphaned brush every frame. The
    /// probe reads the orphaned brush's <c>Phase</c> across a time advance (the test thread IS
    /// the UI thread, so the read is legal) and logs the outcome; the fact pins only what must
    /// hold regardless of where the framework lands.
    /// </summary>
    [Fact]
    public void Old_tree_animation_after_reload_probe()
    {
        _session.Execute(new InitializeCommand { ProtocolVersion = 1, Columns = 80, Rows = 24 });

        Load(Document(" "), id: 1);
        var oldBrush = AnimatedShimmerProbe.LastBrush;
        Assert.NotNull(oldBrush);

        Load(Document("  "), id: 2);
        Assert.NotSame(oldBrush, AnimatedShimmerProbe.LastBrush); // the reload rebuilt the tree

        var phaseBefore = oldBrush!.Phase;
        _session.Execute(new AdvanceTimeCommand { Milliseconds = 200 });
        var phaseAfter = oldBrush.Phase;

        _output.WriteLine(phaseAfter != phaseBefore
            ? $"LEAK: the detached tree's Phase animation still ticks ({phaseBefore} -> {phaseAfter})."
            : "No leak: the detached tree's Phase animation stopped ticking.");
    }
}

/// <summary>
/// The design-surface posture, in-process: production sessions (no test hook) disable the
/// animation scheduler at initialize, so animated content renders SNAPPED — the perpetual Phase
/// animation retracts at Begin (§9.7/AD15), the application reaches idle, ticks change nothing,
/// and a reload of an animated document answers promptly without needing the settle budget.
/// </summary>
public class DesignSurfaceAnimationPostureTests : IDisposable
{
    private readonly List<PreviewEvent> _events = [];
    private readonly PreviewSession _session;

    public DesignSurfaceAnimationPostureTests() => _session = new PreviewSession(_events.Add);

    public void Dispose() => _session.Dispose();

    private void Load(string xaml, long id) => _session.Execute(new LoadXamlCommand
    {
        Id = id,
        Xaml = xaml,
        Assemblies = [typeof(AnimatedShimmerProbe).Assembly.Location],
    });

    [Fact]
    public void Animated_document_renders_snapped_and_reloads_promptly()
    {
        _session.Execute(new InitializeCommand { ProtocolVersion = 1, Columns = 80, Rows = 24 });

        Load(AnimatedReloadTests.Document(" "), id: 1);
        Assert.Contains(_events, e => e is DiagnosticsEvent { ReplyTo: 1, Items.Count: 0 });
        Assert.Contains(_events, e => e is FrameEvent);
        Assert.DoesNotContain(_events, e => e is ErrorEvent);

        // Snapped means SNAPPED: the perpetual Phase animation retracted at Begin, so the brush
        // sits at its base phase and play-mode ticks change nothing — no frame is emitted (an
        // unchanged screen's empty delta is suppressed).
        var brush = AnimatedShimmerProbe.LastBrush;
        Assert.NotNull(brush);
        Assert.Equal(0.0, brush!.Phase);

        var beforeTick = _events.Count;
        _session.Execute(new AdvanceTimeCommand { Milliseconds = 500 });
        Assert.DoesNotContain(_events.Skip(beforeTick), e => e is FrameEvent);
        Assert.Equal(0.0, brush.Phase);

        // The reload answers promptly: with the scheduler disabled the application reaches idle,
        // so the settle exits on its fast path.
        var beforeReload = _events.Count;
        var stall = System.Diagnostics.Stopwatch.StartNew();
        Load(AnimatedReloadTests.Document("  "), id: 2);
        stall.Stop();

        Assert.Contains(_events.Skip(beforeReload), e => e is DiagnosticsEvent { ReplyTo: 2, Items.Count: 0 });
        Assert.True(
            stall.ElapsedMilliseconds < 2000,
            $"Reload of the animated document took {stall.ElapsedMilliseconds} ms under the disabled-animations posture.");
    }
}

/// <summary>
/// The play/pause toggle (setAnimations, task #27). Production sessions start snapped; enabling
/// animations is PROSPECTIVE — the scheduler starts only animations begun while enabled — so the
/// editor pairs setAnimations(true) with a reload to re-instantiate the tree. This pins that a
/// perpetual marquee then advances under advanceTime, and that disabling snaps it back to base.
/// </summary>
public class PlayModeAnimationTests : IDisposable
{
    private readonly List<PreviewEvent> _events = [];
    private readonly PreviewSession _session; // production posture — animations disabled at init

    public PlayModeAnimationTests() => _session = new PreviewSession(_events.Add);

    public void Dispose() => _session.Dispose();

    private void Load(string xaml, long id) => _session.Execute(new LoadXamlCommand
    {
        Id = id,
        Xaml = xaml,
        Assemblies = [typeof(AnimatedShimmerProbe).Assembly.Location],
    });

    [Fact]
    public void SetAnimations_true_then_reload_runs_the_marquee_and_false_snaps_it()
    {
        _session.Execute(new InitializeCommand { ProtocolVersion = 1, Columns = 80, Rows = 24 });

        // Snapped by default: the perpetual Phase animation retracts at Begin.
        Load(AnimatedReloadTests.Document(" "), id: 1);
        Assert.Equal(0.0, AnimatedShimmerProbe.LastBrush!.Phase);

        // Play: lift the gate, then reload so the animation begins WHILE enabled and runs.
        _session.Execute(new SetAnimationsCommand { Enabled = true });
        Load(AnimatedReloadTests.Document(" "), id: 2);
        var running = AnimatedShimmerProbe.LastBrush!;

        var beforeTick = _events.Count;
        _session.Execute(new AdvanceTimeCommand { Milliseconds = 250 });
        Assert.NotEqual(0.0, running.Phase);                          // the marquee advances (~0.25 of a 1 s cycle)
        Assert.Contains(_events.Skip(beforeTick), e => e is FrameEvent); // and the motion emits frames

        // Pause: disabling collapses the running animation back to its base at the settle.
        _session.Execute(new SetAnimationsCommand { Enabled = false });
        Assert.Equal(0.0, running.Phase);
    }

    [Fact]
    public void SetAnimations_before_initialize_is_dropped_not_fatal()
    {
        _session.Execute(new SetAnimationsCommand { Enabled = true });
        Assert.DoesNotContain(_events, e => e is ErrorEvent);
        Assert.Contains(_events, e => e is LogEvent { Level: "debug" });
    }
}
