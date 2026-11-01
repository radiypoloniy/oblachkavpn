namespace Oblachka;

public partial class MainWindow
{
    private readonly Dictionary<string, SubscriptionSchedule> subscriptionSchedules = new();
    private SubscriptionSchedule ScheduleFor(string id)
    {
        if (!subscriptionSchedules.TryGetValue(id, out var schedule)) subscriptionSchedules[id] = schedule = new();
        return schedule;
    }
    private async void PollSubscription()
    {
        if (closed || vmTestActive || !backgroundWorkEnabled || !groups.Any(g => g.IsSubscription) || operation is not null || recoveryWait is not null ||
            !preferences.AutoRefresh) return;
        foreach (var group in groups.Where(g => g.IsSubscription).ToArray())
        {
            if (closed || operation is not null) return;
            if (pendingImport?.Group.Id == group.Id || ForeignConnected && group.Source == ProfileSource.Service) continue;
            if (ScheduleFor(group.Id).IsDue(DateTimeOffset.UtcNow)) await LoadSubscriptionAsync(group.Sources, group.PreferredSource);
        }
    }
    private (SubscriptionState? State, int Revision, bool Busy, bool Remember, string? Announce)? subscriptionShown;
    private void UpdateSubscriptionSummary()
    {
        var state = CurrentSubscription;
        var key = (state, groupRevision, operation is not null, rememberConnections, dismissedAnnounce);
        if (subscriptionShown == key) return;
        subscriptionShown = key;
        Settings.SubscriptionDetails.Text = state?.Summary ?? "Выбран сервер без подписки.";
        Home.PlanText.Text = state?.TermText ?? "";
        Home.PlanSub.Text = state?.TrafficText ?? "";
        var used = state?.Used;
        Home.PlanBars.Visibility = used is null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        Home.PlanBars.Filled = used is { } share ? (int)Math.Round(share * Home.PlanBars.Count) : 0;
        Home.PlanRow.Visibility = state is not null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ApplySubscriptionWarning();
        RefreshSubscriptionSettings();

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
