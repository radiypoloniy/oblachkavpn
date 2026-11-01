using System.Windows.Controls;

namespace Oblachka.Views;

public partial class AutoPoolPage : UserControl
{
    public AutoPoolPage() => InitializeComponent();
}

public sealed class AutoPoolRow(Profile profile, bool included)
{
    public Profile Profile { get; } = profile;
    public bool Included { get; set; } = included;
    public string Name => Profile.DisplayName;
    public string AccessibleName => "«" + Name + "» в автовыборе";
    public string? Note => AutoPool.IsWhitelistBypass(Profile) ? "обход белых списков" : !Profile.IsService ? "сторонняя подписка" : null;
    public System.Windows.Visibility NoteVisibility => Note is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
}
