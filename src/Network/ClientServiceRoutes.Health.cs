namespace Oblachka;

internal sealed partial class ClientServiceRoutes
{
    private readonly Dictionary<int, int> failures = new();
    private readonly HashSet<string> notified = new(StringComparer.Ordinal);
    private CancellationTokenSource? checking;
    private SystemTunnelSession? checkedSession;
    private DateTimeOffset nextCheck;
    private int targetIndex;

    internal event Action<string, string>? RouteBlocked;
    internal event Action? StatusChanged;

    internal (string Text, bool Warning) Status(ServiceRoute route)
    {
        if (owner.Pause.Until is not null && !owner.Connections.MainPaused) return ("На паузе", false);
        if (owner.Tunnel is not { State: SystemTunnelState.Running } tunnel) return ("Не применяется", false);
        if (!MatchesApplied(route)) return ("Ожидает применения", false);
        var pin = tunnel.PinnedServices.FirstOrDefault(r => r.Id == route.Id);
        if (pin?.ExitIndex is not int exit)
        {
            var profile = ClientServerCatalog.Find(owner, route.ProfileId);
            var reason = profile is null ? "Сервер отсутствует"
                : !owner.Tunnels.ServiceProfileAllowed(profile) ? "Сервер исключён из набора правил"
                : tunnel.CanSwitchTo(profile) ? "Выход несовместим с настройками туннеля"
                : "Выход не подготовлен";
            return (reason + " · трафик правила заблокирован", true);
        }
        if (!ReferenceEquals(tunnel, checkedSession) || !failures.TryGetValue(exit, out var count))
            return ("Применён · ожидает проверки", false);
        return count switch
        {
            0 => ("Выход отвечает", false),
            1 => ("Проверяем связь повторно", false),
            _ => ("Проверка связи через выход не проходит", true)
        };
    }

    internal async Task CheckAsync()
    {
        if (checking is not null || owner.Tunnel is not { State: SystemTunnelState.Running, Stats: { } stats } session ||
            session.PinnedServices.Count == 0) return;
        if (!ReferenceEquals(session, checkedSession))
        {
            Reset();
            checkedSession = session;
        }
        if (DateTimeOffset.UtcNow < nextCheck) return;
        nextCheck = DateTimeOffset.UtcNow.AddMinutes(1);
        using var attempt = new CancellationTokenSource();
        checking = attempt;
        try
        {
            var targets = TunnelHealth.Targets;
            var target = targets[targetIndex++ % targets.Length];
            var exits = session.PinnedServices.Select(r => r.ExitIndex).OfType<int>().Distinct().ToArray();
            var results = await Task.WhenAll(exits.Select(async index =>
                (Index: index, Delay: await CoreSelector.DelayAsync(stats, SingBoxTunConfig.DirectTag(index), target, attempt.Token))));
            if (!ReferenceEquals(owner.Tunnel, session) || session.State != SystemTunnelState.Running || attempt.IsCancellationRequested) return;
            foreach (var result in results)
                failures[result.Index] = result.Delay.HasValue ? 0 : Math.Min(2, failures.GetValueOrDefault(result.Index) + 1);
            StatusChanged?.Invoke();
            foreach (var route in owner.TunnelPolicy.ServiceRoutes)
            {
                var (message, warning) = Status(route);
                if (warning && notified.Add(route.Id)) RouteBlocked?.Invoke(route.Id, message);
                if (!warning && session.PinnedServices.FirstOrDefault(p => p.Id == route.Id)?.ExitIndex is int index &&
                    failures.TryGetValue(index, out var count) && count == 0) notified.Remove(route.Id);
            }
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
        finally { if (ReferenceEquals(checking, attempt)) checking = null; }
    }

    internal void Reset()
    {
        checking?.Cancel(); checking = null; checkedSession = null;
        nextCheck = default; failures.Clear(); notified.Clear();
    }
}
