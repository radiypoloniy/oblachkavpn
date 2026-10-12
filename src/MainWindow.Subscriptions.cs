namespace RoutePilot;

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
        var summary = subscriptionMetadata?.Summary(DateTimeOffset.UtcNow);
        Settings.SubscriptionDetails.Text = string.IsNullOrWhiteSpace(summary) ? "Сервер не передал срок и остаток трафика." : summary;
    }
}
