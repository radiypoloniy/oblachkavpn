namespace Oblachka;

internal sealed partial class ClientZapret
{
    internal string ActiveStrategy { get; private set; } = ZapretPlan.NoBypass;
    internal string[] ActiveGroups { get; private set; } = [];
    internal string[] Demoted { get; private set; } = [];
    private string activeNetwork = "";
    private readonly Dictionary<string, int> misses = new(StringComparer.Ordinal);
    private DateTimeOffset nextWatch = DateTimeOffset.MaxValue, repickAt = DateTimeOffset.MaxValue;
    private static readonly TimeSpan WatchEvery = TimeSpan.FromMinutes(5), Recheck = TimeSpan.FromSeconds(30);

    internal void Watch(string strategy, string[] groups, string network)
    {
        ActiveStrategy = strategy; ActiveGroups = groups; activeNetwork = network;
        if (network.Length > 0) Demoted = Demoted.Except(groups).ToArray();
        misses.Clear();
        nextWatch = groups.Length > 0 ? DateTimeOffset.UtcNow + WatchEvery : DateTimeOffset.MaxValue;
    }

    private void Tick()
    {
        if (active is not null || ConnectionBusy) return;
        var now = DateTimeOffset.UtcNow;
        if (RoutingPending || stopWhenSafe)
        {
            if (now >= nextRoutingAttempt) _ = RunAsync(ReconcileAsync);
            return;
        }
        if (!Enabled) return;
        if (afterConnect)
        {
            if (owner.Engine.State is not (ConnectionState.Running or ConnectionState.Verified)) return;
            afterConnect = false;
            modeChanged = true;
        }
        if (modeChanged || now >= repickAt || !Tried)
        {
            if (!NetworkHelperConnection.ServiceRunning)
            {
                modeChanged = false; Tried = true;
                repickAt = now.AddSeconds(10);
                return;
            }
            var force = now >= repickAt;
            repickAt = DateTimeOffset.MaxValue; modeChanged = false;
            _ = RunAsync(token => PickCoreAsync(force, token));
            return;
        }
        if (ActiveGroups.Length == 0 || now < nextWatch) return;
        if (TunnelActive && ProbeRoute is null) { nextWatch = now + Recheck; return; }
        if (NetworkKey != activeNetwork) { _ = RunAsync(token => PickCoreAsync(false, token)); return; }
        _ = RunAsync(CheckAsync);
    }

    private async Task CheckAsync(CancellationToken token)
    {
        var session = owner.Tunnel;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        ZapretProbeResult probe;
        try { probe = await ZapretProbe.RunAsync(ProbeRoute, timeout.Token, quicViaTunnel: TunnelActive); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { nextWatch = DateTimeOffset.UtcNow + Recheck; return; }
        token.ThrowIfCancellationRequested();
        if (!ReferenceEquals(session, owner.Tunnel) || activeNetwork != NetworkKey)
        { nextWatch = DateTimeOffset.UtcNow; return; }
        var passing = ZapretPlan.Passing(probe);
        Journal.Add(ZapretJournalKind.Watch, ActiveStrategy, passing, probe, activeNetwork,
            TunnelActive ? "присмотр через ядро туннеля, TCP и QUIC" : PathNote);
        var lost = new List<string>();
        foreach (var group in ActiveGroups)
        {
            if (passing.Contains(group)) { misses.Remove(group); continue; }
            misses[group] = misses.GetValueOrDefault(group) + 1;
            if (misses[group] >= 2) lost.Add(group);
        }
        nextWatch = DateTimeOffset.UtcNow + (misses.Count > 0 && lost.Count == 0 ? Recheck : WatchEvery);
        if (lost.Count == 0) return;

        var groups = ActiveGroups.Except(lost).ToArray();
        Demoted = Demoted.Union(lost).ToArray();
        foreach (var group in lost) misses.Remove(group);
        Remember(activeNetwork, ActiveStrategy, groups);
        ActiveGroups = groups;
        if (groups.Length == 0) nextWatch = DateTimeOffset.MaxValue;
        domains = ReadDomains(groups);
        stopWhenSafe |= groups.Length == 0 && bypassing;
        Journal.Add(ZapretJournalKind.Demote, ActiveStrategy, groups, network: activeNetwork, note: "в VPN: " + string.Join(",", lost));
        var titles = ZapretPlan.Groups.Where(g => lost.Contains(g.Key)).Select(g => g.Title).ToArray();
        pendingNotice = string.Join(" и ", titles) + (ConnectionActive
            ? " временно через VPN, подбираем обход заново."
            : " перестали открываться напрямую, подбираем обход заново.");
        repickAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await ReconcileAsync(token);
        token.ThrowIfCancellationRequested();
        SetNote(string.Join(", ", titles) + " перестал открываться напрямую. " +
            ZapretPlan.Describe(ActiveStrategy, groups, probe, KeptInVpn));
    }

    private void Remember(string network, string strategy, string[] groups)
    {
        if (!Choices.ContainsKey(network) && Choices.Count >= 64) Choices.Remove(Choices.Keys.First());
        Choices[network] = ZapretPlan.Encode(strategy, groups);
        owner.Persistence.Queue();
    }
}
