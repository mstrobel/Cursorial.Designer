using Cursorial.UI.Controls;

namespace Cursorial.Designer.Tests.UserApp;

/// <summary>
/// The fixture's user-defined control. The E2E tests load a document that references it through
/// <c>clr-namespace:…;assembly=Cursorial.Designer.PreviewHost.Tests.UserApp</c> and assert that
/// it renders and that hit-testing reports its type name — proving the user's assembly and the
/// framework it binds against live in ONE load context.
/// </summary>
public class UserBadge : TextBlock
{
    public UserBadge() => Text = "user badge";
}
