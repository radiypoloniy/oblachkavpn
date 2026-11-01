using System.Windows;
using System.Windows.Controls.Primitives;

namespace Oblachka;

public partial class MainWindow
{
    internal async Task<IReadOnlyList<string>> RunSubscriptionUiFixtureAsync()
    {
        var checks = new List<string>();
        await CheckGroupsAsync(checks);
        CheckAnalytics(checks);
        return checks;
    }

    private void CheckIndependentSubscriptions(List<string> checks)
    {
        var snapshot = CaptureState();
        try
        {
            var external = groups.First(g => g.IsSubscription);
            Replace(external with { Metadata = new(0, 0, 0, null) { WebPageUrl = "https://external.invalid/renew" } });
            SetGroup(new(ProfileGroup.ServiceId, "Сервис", ProfileSource.Service, ["https://service.invalid/sub"])
            {
                Metadata = new(0, 0, 0, DateTimeOffset.UtcNow.AddDays(-1)) { WebPageUrl = "https://service.invalid/renew" }
            }, [ProfilesOf(external.Id)[0]]);
            ScheduleFor(ProfileGroup.ServiceId).AccessDenied();
            ScheduleFor(external.Id).Complete(DateTimeOffset.UtcNow.AddHours(-8), true);
            if (!ScheduleFor(external.Id).IsDue(DateTimeOffset.UtcNow))
                throw new InvalidOperationException("A denied subscription blocked another refresh schedule");
            profile = ProfilesOf(external.Id)[0];
            trafficHistory.Reset(); trafficHistory.Add(100, 900, DateTime.Now);
            RefreshHome(); RefreshSettings(); OpenAnalytics();
            if (CurrentGroup?.Id != external.Id || RenewUrl != "https://external.invalid/renew" ||
                Home.PlanText.Text != "Срок не сообщён" || Home.SubWarn.Visibility != Visibility.Collapsed ||
                Analytics.PlanTitle.Text != external.Name || !Analytics.PlanRemaining.Text.Contains("без ограничения") ||
                Settings.Subscription.SubscriptionTitle.Text != external.Name || !ConnectButton.IsEnabled)
                throw new InvalidOperationException("Expired service subscription overrides external subscription");

            var now = DateTimeOffset.UtcNow;
            Replace(CurrentGroup! with { Metadata = new(0, 0, 10L * 1073741824, now.AddDays(5)) });
            RefreshHome();
            Home.SubWarnHide.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (Home.SubWarn.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Dismissal did not hide warning");
            var store = new ClientStateStore(Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"), "dismissal.bin"));
            store.Save(CaptureState());
            ApplySnapshot(store.Load()!); RefreshHome();
            if (Home.SubWarn.Visibility != Visibility.Collapsed || CurrentGroup?.DismissedWarning is null)
                throw new InvalidOperationException($"Dismissal did not survive saving: selected={profile?.Group}, key={CurrentSubscription?.WarningKey}, hidden={CurrentGroup?.DismissedWarning}, snapshot={store.Load()!.SelectedGroup}");
            profile = ProfilesOf(ProfileGroup.ServiceId)[0]; RefreshHome(); RefreshAnalytics();
            if (Analytics.TotalText.Text != TrafficMeter.Size(1000) || !ConnectButton.IsEnabled)
                throw new InvalidOperationException("Subscription state changed local traffic or blocked connection");
            if (Home.SubWarn.Visibility != Visibility.Visible || RenewUrl != "https://service.invalid/renew")
                throw new InvalidOperationException("Dismissing another group suppressed expired service warning");
            profile = ProfilesOf(external.Id)[0];
            Replace(CurrentGroup! with { Metadata = CurrentGroup!.Metadata! with { Expires = now.AddHours(12) } });
            RefreshHome();
            if (Home.SubWarn.Visibility != Visibility.Visible) throw new InvalidOperationException("Critical threshold did not show warning again");
            Home.SubWarnHide.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Replace(CurrentGroup! with { Metadata = CurrentGroup!.Metadata! with { Expires = now.AddDays(7) } });
            RefreshHome();
            if (Home.SubWarn.Visibility != Visibility.Visible) throw new InvalidOperationException("Renewal did not reset dismissal");
            Replace(CurrentGroup! with { Metadata = new(0, 4L * 1073741824, 6L * 1073741824, null) });
            RefreshHome(); Home.SubWarnHide.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Replace(CurrentGroup! with { Metadata = CurrentGroup!.Metadata! with { Download = 3L * 1073741824 } });
            RefreshHome();
            if (Home.SubWarn.Visibility != Visibility.Visible) throw new InvalidOperationException("Refill did not reset low traffic dismissal");
            profile = ProfilesOf(ProfileGroup.ManualId)[0]; RefreshHome(); RefreshSettings(); RefreshAnalytics();
            if (CurrentGroup is not null || RenewUrl is not null || Home.PlanRow.Visibility != Visibility.Collapsed ||
                Home.SubWarn.Visibility != Visibility.Collapsed || Analytics.PlanCard.Visibility != Visibility.Collapsed ||
                Settings.Subscription.ActiveCard.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Manual server inherited a subscription");
            RemoveGroup(external.Id);
            if (ServiceGroup?.Metadata?.Expires >= now || ProfilesOf(ProfileGroup.ManualId).Count != 2)
                throw new InvalidOperationException("Removing external subscription changed other groups");
            checks.Add("Subscriptions are independent across home, settings, analytics and removal; dismissals persist and reset on escalation, renewal and refill");
        }
        finally { CloseAnalytics(); ApplySnapshot(snapshot); }
    }
}
