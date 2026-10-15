namespace Oblachka;

public partial class MainWindow
{
    private readonly SubscriptionSchedule subscriptionSchedule = new();
    private async void PollSubscription()
    {
        if (closed || vmTestActive || !backgroundWorkEnabled || !groups.Any(g => g.IsSubscription) || operation is not null || recoveryWait is not null ||
            pendingImport is not null || Settings.AutoRefresh.IsChecked != true || !subscriptionSchedule.IsDue(DateTimeOffset.UtcNow)) return;
        await RefreshAllAsync();
    }
    private void UpdateSubscriptionSummary()
    {
        var now = DateTimeOffset.UtcNow;
        var summary = subscriptionMetadata?.Summary(now);
        Settings.SubscriptionDetails.Text = string.IsNullOrWhiteSpace(summary) ? "Сервер не передал срок и остаток трафика." : summary;

        // Срок на главном экране: раньше он был виден только в настройках.
        var shortSummary = subscriptionMetadata?.ShortSummary(now);
        Home.PlanText.Text = string.IsNullOrWhiteSpace(shortSummary) ? "" : shortSummary;
        ApplySubscriptionWarning();

        // Объявление панели. Текст чужой, поэтому он уже очищен и обрезан в SubscriptionMetadata,
        // показывается как обычный текст и только при наличии подписки сервиса.
        var announce = serviceProfiles.Count > 0 ? ServiceGroup?.Metadata?.Announce : null;
        Home.AnnounceText.Text = announce ?? "";
        // Скрытое крестиком объявление не возвращается, пока сервис не пришлёт новое.
        Home.AnnounceCard.Visibility = announce is null || announce == dismissedAnnounce
            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }
}
