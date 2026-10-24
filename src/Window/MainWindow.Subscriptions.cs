namespace Oblachka;

public partial class MainWindow
{
    private readonly SubscriptionSchedule subscriptionSchedule = new();
    private async void PollSubscription()
    {
        if (closed || vmTestActive || !backgroundWorkEnabled || !groups.Any(g => g.IsSubscription) || operation is not null || recoveryWait is not null ||
            pendingImport is not null || ForeignConnected || Settings.AutoRefresh.IsChecked != true || !subscriptionSchedule.IsDue(DateTimeOffset.UtcNow)) return;
        await RefreshAllAsync();
    }
    private void UpdateSubscriptionSummary()
    {
        var now = DateTimeOffset.UtcNow;
        var summary = subscriptionMetadata?.Summary(now);
        Settings.SubscriptionDetails.Text = string.IsNullOrWhiteSpace(summary) ? "Сервер не передал срок и остаток трафика." : summary;

        // Срок и остаток — в карточке «сегодня и подписка» внизу боковой колонки.
        var shortSummary = subscriptionMetadata?.ShortSummary(now);
        Home.PlanText.Text = string.IsNullOrWhiteSpace(shortSummary) ? "" : shortSummary;
        var (left, used) = subscriptionMetadata?.Remaining() ?? (null, null);
        Home.PlanSub.Text = left ?? (subscriptionMetadata?.Total == 0 ? "трафик без ограничения" : "");
        Home.PlanBars.Visibility = used is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        Home.PlanBars.Filled = used is { } share ? (int)Math.Round(share * Home.PlanBars.Count) : 0;
        Home.PlanRow.Visibility = Home.PlanText.Text.Length > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ApplySubscriptionWarning();

        // Объявление панели. Текст чужой, поэтому он уже очищен и обрезан в SubscriptionMetadata,
        // показывается как обычный текст и только при наличии подписки сервиса.
        var announce = serviceProfiles.Count > 0 ? ServiceGroup?.Metadata?.Announce : null;
        Home.AnnounceText.Text = announce ?? "";
        // Кнопка объявления (announce-url, banner-button-*) — только с проверенной HTTPS-ссылкой.
        var provider = ServiceGroup?.Metadata?.Provider;
        Home.AnnounceLink.Content = provider?.AnnounceButton ?? "Подробнее";
        Home.AnnounceLink.Visibility = provider?.AnnounceUrl is not null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        // DoH для адресов серверов, если сервис его попросил (server-address-resolve-*).
        DohResolver.Preferred = provider?.Resolve;
        // Скрытое крестиком объявление не возвращается, пока сервис не пришлёт новое.
        Home.AnnounceCard.Visibility = announce is null || announce == dismissedAnnounce
            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }
}
