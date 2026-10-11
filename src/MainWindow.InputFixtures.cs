using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RoutePilot;

public partial class MainWindow
{
    private void CheckSubscriptionInput(List<string> checks)
    {
        var field = Onboarding.SubscriptionUrl;
        field.Text = "https://fixture.invalid/sub/synthetic-token";
        field.CaretIndex = field.Text.Length;
        DesignPreview.Render(this, Path.Combine(AppContext.BaseDirectory, "preview-input.png"));
        var host = (ScrollViewer)field.Template.FindName("PART_ContentHost", field);
        var caret = field.GetRectFromCharacterIndex(0);
        var hostOrigin = host.TranslatePoint(new Point(), field);
        if (caret.IsEmpty || caret.Height < 10 || caret.Top < hostOrigin.Y ||
            caret.Bottom > hostOrigin.Y + host.ActualHeight + 0.5)
            throw new InvalidOperationException($"Subscription text is clipped: caret={caret}, host={hostOrigin}, height={host.ActualHeight}");
        field.SelectAll();
        field.SelectedText = "https://fixture.invalid/sub/replacement";
        if (field.Text != "https://fixture.invalid/sub/replacement")
            throw new InvalidOperationException("Subscription field cannot replace selected text");
        field.Clear();
        checks.Add("Subscription text and caret fit the input viewport; selected text remains editable");
        if (WindowStyle != WindowStyle.SingleBorderWindow || ResizeMode != ResizeMode.CanResize ||
            System.Windows.Shell.WindowChrome.GetWindowChrome(this) is not null || Onboarding.FindName("PasteUrlButton") is not null || !Onboarding.ContinueButton.IsDefault)
            throw new InvalidOperationException("Window still uses custom caption buttons or redundant paste action");
        checks.Add("Native Windows caption handles window actions; onboarding retains standard editable input without a duplicate paste button");
    }
}
