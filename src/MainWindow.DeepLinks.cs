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

    private bool ConfirmLink(string what) => AppDialog.Confirm(this, "Добавить " + what + "?",
        "Ссылку открыл браузер или другая программа. Добавляйте, только если получили её от своего сервиса.", "Добавить");
}
