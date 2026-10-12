using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RoutePilot;

public partial class MainWindow
{
    private void CheckSubscriptionInput(List<string> checks)
    {
        var field = Onboarding.KeyInput;
        var serviceUrl = "https://" + ServiceBrand.SubscriptionHosts[0] + ServiceBrand.SubscriptionPath + "synthetic-token";
        field.Text = serviceUrl;
        field.CaretIndex = field.Text.Length;
        DesignPreview.Render(this, Path.Combine(AppContext.BaseDirectory, "preview-input.png"));
        var host = (ScrollViewer)field.Template.FindName("PART_ContentHost", field);
        var caret = field.GetRectFromCharacterIndex(0);
        var hostOrigin = host.TranslatePoint(new Point(), field);
        if (caret.IsEmpty || caret.Height < 10 || caret.Top < hostOrigin.Y ||
            caret.Bottom > hostOrigin.Y + host.ActualHeight + 0.5)
            throw new InvalidOperationException($"Subscription text is clipped: caret={caret}, host={hostOrigin}, height={host.ActualHeight}");
        field.SelectAll();
        field.SelectedText = "hRDbej1hw9YC7rEc-QktKA5Rh";
        if (field.Text != "hRDbej1hw9YC7rEc-QktKA5Rh")
            throw new InvalidOperationException("Subscription field cannot replace selected text");
        checks.Add("Subscription text and caret fit the input viewport; selected text remains editable");

        // Подписка другого сервиса — не ошибка ввода: у неё отдельное место в интерфейсе,
        // и сетевого запроса по чужому хосту клиент не делает.
        field.Text = "https://sub.other-service.invalid/sub/foreign-token";
        Onboarding.ContinueButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (!DetailText.Text.Contains("Другие подписки") || profile is not null || engine.ProcessId is not null)
            throw new InvalidOperationException("Foreign subscription host is not routed to the external-subscription section: " + DetailText.Text);
        checks.Add("Foreign subscription host is rejected with a pointer to external subscriptions, without a network request");
        field.Clear();
        if (WindowStyle != WindowStyle.SingleBorderWindow || ResizeMode != ResizeMode.CanResize ||
            System.Windows.Shell.WindowChrome.GetWindowChrome(this) is not null || Onboarding.FindName("PasteUrlButton") is not null || !Onboarding.ContinueButton.IsDefault)
            throw new InvalidOperationException("Window still uses custom caption buttons or redundant paste action");
        checks.Add("Native Windows caption handles window actions; onboarding retains standard editable input without a duplicate paste button");
    }
}
