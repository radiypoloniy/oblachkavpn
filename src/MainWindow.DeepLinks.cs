using System.Windows;
using System.Windows.Controls;

namespace Oblachka;

// Ссылки oblachka:// (DeepLink): с сайта или из бота продавца подписки. Ссылка открывает окно
// и предлагает добавить подписку; молча ничего не загружается — ссылку мог подсунуть кто угодно.
public partial class MainWindow
{
    private string? pendingLink;

    public void QueueDeepLink(string link)
    {
        pendingLink = link;
        if (IsLoaded) _ = HandleDeepLinkAsync();
        else Loaded += async (_, _) => await HandleDeepLinkAsync();
    }

    private async Task HandleDeepLinkAsync()
    {
        var link = pendingLink; pendingLink = null;
        if (link is null || closed) return;
        ShowWindow();
        var value = DeepLink.Parse(link);
        if (value is null) { DetailText.Text = "Ссылка не распознана: нужна ссылка подписки https:// или ключ."; return; }
        var kind = ServiceKey.Classify(value);
        var host = Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : ServiceBrand.Name;
        if (!ConfirmLink(kind == ServiceKey.InputKind.ExternalUrl ? "подписку " + host : "подписку " + ServiceBrand.Name)) return;
        if (operation is not null) { DetailText.Text = "Дождитесь окончания текущей операции и откройте ссылку снова."; return; }
        // Первый запуск — как ввод в поле ключа: загрузить и подключить. Иначе подписка заменяет
        // свой набор (сервиса или сторонний), выбор и подключение остаются за человеком.
        if (profile is null) { await ContinueWithAsync(value, clearField: false); return; }
        try
        {
            if (kind == ServiceKey.InputKind.ExternalUrl) await LoadSubscriptionAsync(SubscriptionMirrors.ParseSources(value), 0);
            else await LoadSubscriptionAsync(new[] { ServiceKey.ToSubscriptionUrl(value) }, 0);
            if (Settings.Visibility == Visibility.Visible) CloseSettings();
            Navigate("Connection");
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }

    private bool ConfirmLink(string what)
    {
        var dialog = new Window
        {
            Owner = IsVisible ? this : null, Title = "Добавить в " + ServiceBrand.Name, Width = 460, Height = 230, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, Background = Background, FontFamily = FontFamily
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Добавить " + what + "?", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Foreground, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = "Ссылку открыл браузер или другая программа. Добавляйте, только если получили её от своего сервиса.",
            Foreground = Foreground, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 18)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var add = new Button { Content = "Добавить", IsDefault = true, MinWidth = 110 };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        add.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(add); buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel; StyleDialog(dialog);
        return dialog.ShowDialog() == true;
    }
}
