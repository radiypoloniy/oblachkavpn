using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Oblachka;

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

        // Одно поле на всё: что вставлено, определяется без сети.
        var cases = new (string Text, ServiceKey.InputKind Kind)[]
        {
            ("hRDbej1hw9YC7rEc-QktKA5Rh", ServiceKey.InputKind.ServiceKey),
            (serviceUrl, ServiceKey.InputKind.ServiceUrl),
            ("https://sub.other-service.invalid/sub/foreign-token", ServiceKey.InputKind.ExternalUrl),
            ("sub.other-service.invalid/sub/foreign-token", ServiceKey.InputKind.ExternalUrl),
            ("vless://11111111-1111-4111-8111-111111111111@example.invalid:443?security=tls#x", ServiceKey.InputKind.Profiles),
            ("{\"outbounds\":[]}", ServiceKey.InputKind.Profiles)
        };
        foreach (var (text, kind) in cases)
            if (ServiceKey.Classify(text) != kind) throw new InvalidOperationException($"Key field misclassified {text}: {ServiceKey.Classify(text)}");
        checks.Add("One key field tells a service key, service and foreign subscription links and profiles apart without a network request");
        field.Clear();
        // Редизайн: окно 420×660 без изменения размера, шапку рисуют экраны; свернуть и закрыть
        // остаются доступными и нажимаются поверх области перетаскивания.
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (ResizeMode != ResizeMode.CanMinimize || Width != 420 || Height > 660 || chrome is null || chrome.CaptionHeight != 56 ||
            !System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(MinimizeButton) || !System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(CloseWindowButton) ||
            !System.Windows.Shell.WindowChrome.GetIsHitTestVisibleInChrome(Home.SettingsButton) ||
            Onboarding.FindName("PasteUrlButton") is not null || !Onboarding.ContinueButton.IsDefault)
            throw new InvalidOperationException("Window chrome or onboarding input does not match the redesign");
        checks.Add("420×660 window keeps working minimize, close and settings in its own header; onboarding has no duplicate paste button");
    }
}
