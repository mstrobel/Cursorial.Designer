using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.Tests.PreviewHost;

/// <summary>
/// The design-surface animation posture, end-to-end against the real split host (which always
/// runs it — the <c>keepAnimationsEnabled</c> hook is in-process only): a document with a
/// perpetually animated <c>PhaseShiftedBrush</c> (the UserApp fixture's <c>AnimatedShimmer</c> —
/// figlet text over a marquee gradient; the headless profiles advertise no OSC 66, matching the
/// maintainer's environment) renders SNAPPED, play-mode ticks change nothing, and the reload
/// that starved the pre-fix host for its entire settle budget answers promptly.
/// </summary>
public class AnimatedReloadEndToEndTests
{
    /// <summary>The animated document; <paramref name="separator"/> varies attribute formatting only.</summary>
    private static string Document(string separator) =>
        $"""
         <StackPanel xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml"
                     xmlns:user="clr-namespace:Cursorial.Designer.Tests.UserApp;assembly=Cursorial.Designer.PreviewHost.Tests.UserApp">
             <user:AnimatedShimmer/>
             <TextBlock{separator}Text="steady"/>
         </StackPanel>
         """;

    [Fact]
    public async Task Animated_document_previews_snapped_and_reload_answers_within_five_seconds()
    {
        var userDir = HostProcessHarness.FixturePath("UserAppOutputDirectory");
        using var host = new HostProcessHarness("--user-dir", userDir);
        Assert.IsType<ReadyEvent>(await host.NextEvent());

        await host.Send(new InitializeCommand { ProtocolVersion = 1, Columns = 80, Rows = 24 });
        Assert.IsType<FrameEvent>(await host.NextEvent());

        var assemblies = new[] { Path.Combine(userDir, "Cursorial.Designer.PreviewHost.Tests.UserApp.dll") };
        await host.Send(new LoadXamlCommand { Id = 1, Xaml = Document(" "), Assemblies = assemblies });
        Assert.IsType<DependenciesEvent>(await host.NextEvent());
        Assert.Empty(Assert.IsType<DiagnosticsEvent>(await host.NextEvent()).Items);
        Assert.Contains("steady", HostProcessHarness.FrameText(Assert.IsType<FrameEvent>(await host.NextEvent())));

        // Snapped means snapped: the perpetual Phase animation retracted at Begin, so play-mode
        // ticks change no cells and emit no frames — the hit test's reply must be the NEXT
        // meaningful event after two ticks, with no frame in between.
        await host.Send(new AdvanceTimeCommand { Id = 10, Milliseconds = 250 });
        await host.Send(new AdvanceTimeCommand { Id = 11, Milliseconds = 250 });
        await host.Send(new HitTestCommand { Id = 12, Column = 1, Row = 0 });
        Assert.IsType<HitTestResultEvent>(await host.NextEvent());

        // The reload — the editor reformatting attributes, no value changes — answers promptly.
        // The pre-mitigation, pre-budget host burned its full settle budget here first (~35 s on
        // the maintainer's document; 6+ s for a 300×100 surface in the live capture).
        await host.Send(new LoadXamlCommand { Id = 2, Xaml = Document("  "), Assemblies = assemblies });
        var reply = host.NextEvent();
        var winner = await Task.WhenAny(reply, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(
            ReferenceEquals(reply, winner),
            "The host did not answer the reload within 5 s — reload-while-animating starved the command loop.");
        Assert.IsType<DependenciesEvent>(await reply);
        Assert.Empty(Assert.IsType<DiagnosticsEvent>(await host.NextEvent()).Items);

        // A value-identical document settles to an identical screen, and an empty delta is
        // suppressed — so the post-reload liveness pin is a query round-trip, not a frame.
        await host.Send(new HitTestCommand { Id = 13, Column = 1, Row = 0 });
        Assert.IsType<HitTestResultEvent>(await host.NextEvent());

        await host.ShutdownCleanly();
    }
}
