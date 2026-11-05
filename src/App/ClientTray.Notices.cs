using Forms = System.Windows.Forms;

namespace Oblachka;

internal sealed partial class ClientTray
{
    private enum Notice { ServerSwitched, NetworkClosed, Expiring, BypassBroken, ProtectionRepaired, ProxyRepaired, ProxyLost, PauseEnded, Update, NetworkSecured, ServiceRouteBlocked }

    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExpiryInterval = TimeSpan.FromMinutes(1);
    private readonly Dictionary<(Notice Kind, string? Scope), DateTimeOffset> shown = new();
    private (TrayCommand Command, string? RenewUrl) noticeTarget = (TrayCommand.Notice, null);
    private Timer? expiryTimer;
    private int expiryPosted;

    internal void StartNotices()
    {
        if (disposed || expiryTimer is not null) return;
        owner.Recovery.Completed += RecoveryCompleted;
        owner.ProtectionRepaired += ProtectionRepaired;
        owner.Proxy.Changed += ProxyChanged;
        owner.Pause.Resuming += PauseResuming;
        owner.Updates.AvailableInBackground += UpdateAvailable;
        owner.Networks.Securing += NetworkSecuring;
        owner.Zapret.BypassBroken += BypassBroken;
        owner.ServiceRoutes.RouteBlocked += ServiceRouteBlocked;
        expiryTimer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref expiryPosted, 1) != 0) return;
            context.Post(_ =>
            {
                Interlocked.Exchange(ref expiryPosted, 0);
                if (!disposed) PollExpiry();
            }, null);
        }, null, ExpiryInterval, ExpiryInterval);
    }

    private void StopNotices()
    {
        if (expiryTimer is null) return;
        expiryTimer.Dispose(); expiryTimer = null;
        owner.Recovery.Completed -= RecoveryCompleted;
        owner.ProtectionRepaired -= ProtectionRepaired;
        owner.Proxy.Changed -= ProxyChanged;
        owner.Pause.Resuming -= PauseResuming;
        owner.Updates.AvailableInBackground -= UpdateAvailable;
        owner.Networks.Securing -= NetworkSecuring;
        owner.Zapret.BypassBroken -= BypassBroken;
        owner.ServiceRoutes.RouteBlocked -= ServiceRouteBlocked;
        shown.Clear(); noticeTarget = (TrayCommand.Notice, null);
    }

    private bool CanNotify => !disposed && Icon.Visible && !owner.Activity.VmTestActive &&
        owner.Preferences.Notices && CurrentView()?.Foreground != true;

    private bool Notify(Notice kind, string title, string text, bool warning = false, string? scope = null,
        TrayCommand command = TrayCommand.Notice, string? renewUrl = null)
    {
        if (!CanNotify) return false;
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in shown.Where(p => now - p.Value >= Quiet).Select(p => p.Key).ToArray()) shown.Remove(expired);
        if (!shown.TryAdd((kind, scope), now)) return false;
        noticeTarget = (command, renewUrl);
        Icon.ShowBalloonTip(8000, title, text, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
        return true;
    }

    internal void ShowBackgroundHint()
    {
        noticeTarget = (TrayCommand.Notice, null);
        Icon.ShowBalloonTip(4000, ServiceBrand.Name, "Облачка продолжают работать в трее. Выйти — через меню значка: «Завершить».", Forms.ToolTipIcon.Info);
    }

    private bool TunnelActive => owner.Tunnel is { State: not (SystemTunnelState.Idle or SystemTunnelState.Failed) };

    private void PollExpiry()
    {
        if (!CanNotify) return;
        var profiles = owner.Profiles;
        var group = ProfileGroups.Active(profiles.Groups, profiles.Selected, profiles.Running,
            owner.Engine.State != ConnectionState.Idle || TunnelActive);
        if (group is not { Metadata.Expires: { } expires }) return;
        var now = DateTimeOffset.UtcNow;
        var left = expires - now;
        if (left <= TimeSpan.Zero || left > TimeSpan.FromDays(3)) return;
        var state = SubscriptionState.For(group, now);
        if (state.WarningKey == group.DismissedWarning) return;
        var stage = left <= TimeSpan.FromDays(1) ? 1 : 3;
        var key = expires.ToUnixTimeSeconds() + "|" + stage;
        if (group.ExpiryNotice == key) return;
        var url = state.RenewUrl;
        var when = stage == 1 ? "в течение суток" : "через " + Math.Max(2, (int)Math.Ceiling(left.TotalDays)) + " дня";
        if (Notify(Notice.Expiring, group.Name + ": срок истекает " + when,
            url is not null ? "Нажмите, чтобы продлить эту подписку." : "Обновите данные подписки или выберите другой сервер.",
            scope: group.Id, renewUrl: url))
        {
            owner.Imports.Replace(group with { ExpiryNotice = key });
            owner.Persistence.Queue();
        }
    }

    private void RecoveryCompleted(RecoveryOutcome result)
    {
        if (owner.Activity.ManualStop) return;
        switch (result.Kind)
        {
            case RecoveryOutcomeKind.RequiresReconnect when result.Selected is not null && CurrentView() is not null:
                Notify(Notice.ServerSwitched, "Переподключаем VPN", "Пересоздаём сессию: на это время защита снимается.");
                break;
            case RecoveryOutcomeKind.PathBroken:
                Notify(Notice.NetworkClosed, "Интернет через VPN не работает", "Нажмите и выберите «Выключить VPN и открыть интернет».", warning: true);
                break;
            case RecoveryOutcomeKind.Recovered when result.Selected is { } selected && !ReferenceEquals(result.Previous, selected):
                Notify(Notice.ServerSwitched, "Сервер сменился", result.Previous?.DisplayName + " перестал отвечать — теперь " + selected.DisplayName + ".");
                break;
            case RecoveryOutcomeKind.NotFound:
                Notify(Notice.NetworkClosed, TunnelActive ? "Интернет закрыт" : "VPN не работает",
                    "Рабочий сервер не найден. Нажмите, чтобы выбрать другой или выключить VPN.", warning: true);
                break;
        }
    }

    private void ProtectionRepaired() => context.Post(_ =>
        Notify(Notice.ProtectionRepaired, "Защита от утечек снова на месте",
            "Её снял Windows или другая программа, Облачка поставила заново. Мимо VPN ничего не ушло.", warning: true), null);

    private void ProxyChanged(ProxyChange change)
    {
        if (change.Kind == ProxyChangeKind.Repaired)
            Notify(Notice.ProxyRepaired, "Прокси снова через VPN", "Другая программа поменяла прокси Windows, Облачка вернула свой.");
        else if (change.Kind == ProxyChangeKind.Lost)
            Notify(Notice.ProxyLost, "Браузеры идут мимо VPN", "Другая программа меняет прокси Windows. Закройте её или включите «Весь компьютер».", warning: true);
    }

    private void UpdateAvailable(AppRelease release) =>
        Notify(Notice.Update, "Вышла " + release.DisplayName, "Нажмите, чтобы обновить.", command: TrayCommand.Update);
    private void PauseResuming() => Notify(Notice.PauseEnded, "Пауза кончилась", "VPN снова включается.");
    private void NetworkSecuring(string message) => Notify(Notice.NetworkSecured, "VPN включается", message);
    private void BypassBroken(string message) => Notify(Notice.BypassBroken, "Обход перестал работать", message);
    private void ServiceRouteBlocked(string id, string message)
    {
        var route = owner.TunnelPolicy.ServiceRoutes.FirstOrDefault(r => r.Id == id);
        var title = route is null ? ClientServiceRoutes.Title(id) : ClientServiceRoutes.Title(route);
        Notify(Notice.ServiceRouteBlocked, title, message, warning: true, scope: id);
    }
}
