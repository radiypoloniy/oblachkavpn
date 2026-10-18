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
        var sources = kind == ServiceKey.InputKind.ExternalUrl ? SubscriptionMirrors.ParseSources(value) : new[] { ServiceKey.ToSubscriptionUrl(value) };
        var id = ProfileGroup.IdFor(sources, ServiceKey.SourceOf(sources));
        var what = kind == ServiceKey.InputKind.ExternalUrl ? "подписку " + host : "подписку " + ServiceBrand.Name;
        // Страница подписки открывает ссылку сама при каждой загрузке: убранная подписка не должна
        // возвращаться одним нажатием «Добавить» по привычке.
        if (removedSubscriptions.Contains(id)
            ? !AppDialog.Confirm(this, "Вернуть " + what + "?", "Вы её убрали. Ссылку снова открыла страница в браузере — так бывает, когда страницу перезагружают. Вернуть подписку?", "Вернуть", "Не возвращать")
            : !ConfirmLink(what)) return;
        if (operation is not null) { DetailText.Text = "Дождитесь окончания текущей операции и откройте ссылку снова."; return; }
        // Первый запуск — как ввод в поле ключа: загрузить и подключить. Иначе подписка заменяет
        // свой набор (сервиса или сторонний), выбор и подключение остаются за человеком.
        if (profile is null) { await ContinueWithAsync(value, clearField: false); return; }
        try
        {
            await LoadSubscriptionAsync(sources, 0);
            if (Settings.Visibility == Visibility.Visible) CloseSettings();
            Navigate("Connection");
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }

    private bool ConfirmLink(string what) => AppDialog.Confirm(this, "Добавить " + what + "?",
        "Ссылку открыл браузер или другая программа. Добавляйте, только если получили её от своего сервиса.", "Добавить");
}
