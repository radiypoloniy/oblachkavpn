namespace Oblachka;

public partial class MainWindow
{
    private readonly SubscriptionSchedule subscriptionSchedule = new();
    private SubscriptionMetadata? subscriptionMetadata;
    private async void PollSubscription()
    {
        if (closed || vmTestActive || !backgroundWorkEnabled || subscriptionSources.Count == 0 || operation is not null || recoveryWait is not null ||
            pendingImport is not null || Settings.AutoRefresh.IsChecked != true || !subscriptionSchedule.IsDue(DateTimeOffset.UtcNow)) return;
        await LoadSubscriptionAsync(subscriptionSources, preferredSource);
    }
    private void UpdateSubscriptionSummary()
    {
        var now = DateTimeOffset.UtcNow;
        var summary = subscriptionMetadata?.Summary(now);
        Settings.SubscriptionDetails.Text = string.IsNullOrWhiteSpace(summary) ? "Сервер не передал срок и остаток трафика." : summary;

        // Срок на главном экране: раньше он был виден только в настройках.
        var shortSummary = subscriptionMetadata?.ShortSummary(now);
        Connection.PlanTile.Text = string.IsNullOrWhiteSpace(shortSummary) ? "Нет данных" : shortSummary;

        // Объявление панели. Текст чужой, поэтому он уже очищен и обрезан в SubscriptionMetadata,
        // показывается как обычный текст и только при наличии подписки сервиса.
        var announce = serviceProfiles.Count > 0 ? subscriptionMetadata?.Announce : null;
        Connection.AnnounceText.Text = announce ?? "";
        Connection.AnnounceTitle.Text = subscriptionMetadata?.Title is { } title ? title : "Сообщение сервиса";
        Connection.AnnounceCard.Visibility = announce is null
            ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }
}
