using Cursorial.Animation;
using Cursorial.Media;
using Cursorial.Rendering.Media;
using Cursorial.Text;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Media;

namespace Cursorial.Designer.Tests.UserApp;

/// <summary>
/// The fixture's perpetually-animated control: scaled text (figlet-rendered under the headless
/// capability profiles, which advertise no OSC 66 text sizing) over a <see cref="PhaseShiftedBrush"/>
/// marquee whose <c>Phase</c> is driven by a Forever animation begun in code when the element
/// attaches — the shape a user's shimmer control takes, because a storyboard track targets
/// <see cref="UIElement"/>s and cannot reach a brush's sub-object property from markup. The
/// animation is deliberately NOT stopped on detach, mirroring real documents (nothing in a
/// markup-authored document stops a decorative animation when the designer swaps trees).
/// </summary>
public class AnimatedShimmer : Border
{
    private readonly PhaseShiftedBrush _brush = new(new LinearGradientBrush(
        Color.FromRgb(30, 30, 60), Color.FromRgb(220, 220, 255), spread: GradientSpread.Repeat));

    public AnimatedShimmer()
    {
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
