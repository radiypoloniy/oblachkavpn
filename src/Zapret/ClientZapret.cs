namespace Oblachka;

// Обход принадлежит runtime. Все команды и результаты проверок обрабатываются
// в его контексте, последовательно с пользовательскими переключателями.
internal sealed partial class ClientZapret(ClientRuntime owner) : IDisposable
{
    private readonly SemaphoreSlim commands = new(1);
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? active;
    private Timer? timer;
    private int tickPosted;
    private bool disposed, afterConnect, modeChanged, stopWhenSafe;
    private DateTimeOffset nextRoutingAttempt;
    private string? observedNetwork, pendingNotice;
    private string[] domains = [];
    private string tag = "";
    private bool bypassing;
    private ZapretStatus? status;
    private ZapretJournal? journal;
    private ZapretJournal Journal => journal ??= new(ZapretJournal.DefaultPath);

    internal bool Enabled => owner.TunnelPolicy.Zapret;
    internal bool Busy { get; private set; }
    internal bool Tried { get; private set; }
    internal string Note { get; private set; } = "";
    internal string[] RoutedDomains => Enabled && !Busy && (!bypassing || status?.Running == true) ? domains : [];
    internal string[] Candidates => ZapretPlan.Groups.Select(g => g.Key).Where(k => !KeptInVpn.Contains(k)).ToArray();
    internal HashSet<string> KeptInVpn => owner.Workspace.ZapretVpnGroups;
    internal IReadOnlyList<ZapretJournalEntry> History => Journal.Entries;
    private Dictionary<string, string> Choices => owner.Workspace.ZapretChoices;
    private bool TunnelActive => owner.Tunnel is { State: not (SystemTunnelState.Idle or SystemTunnelState.Failed) };
    private bool ConnectionActive => owner.Engine.State != ConnectionState.Idle || TunnelActive;
    private bool RoutingPending => ConnectionActive && !owner.Tunnels.SentZapretDomains.SequenceEqual(RoutedDomains);
    internal bool UpdatingRoutes => RoutingPending;
    internal string[] AppliedDomains => ConnectionActive ? owner.Tunnels.SentZapretDomains : RoutedDomains;
    private bool ConnectionBusy => owner.Activity.Operation is not null || owner.Activity.VmTestActive ||
        owner.Connections.IsActive || owner.Connections.IsStopping || owner.TunnelStopping;

    internal event Action? Changed;
    internal event Action<string>? BypassBroken;

    internal void Start(SynchronizationContext context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (timer is not null) return;
        observedNetwork = owner.Networks.Current?.Key;
        owner.Networks.Changed += NetworkChanged;
        timer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref tickPosted, 1) != 0) return;
            context.Post(_ =>
            {
                Interlocked.Exchange(ref tickPosted, 0);
                if (disposed) return;
                Tick();
                Schedule();
            }, null);
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Schedule();
    }

    private void Schedule() => timer?.Change(NextDelay(DateTimeOffset.UtcNow), Timeout.InfiniteTimeSpan);

    private TimeSpan NextDelay(DateTimeOffset now)
    {
        if (active is not null || !Enabled && !RoutingPending && !stopWhenSafe) return Timeout.InfiniteTimeSpan;
        if (ConnectionBusy || afterConnect || modeChanged || Enabled && !Tried) return TimeSpan.FromSeconds(1);
        var due = RoutingPending || stopWhenSafe ? nextRoutingAttempt : nextWatch < repickAt ? nextWatch : repickAt;
        if (due == DateTimeOffset.MaxValue) return Timeout.InfiniteTimeSpan;
        return due <= now ? TimeSpan.FromMilliseconds(1) : due - now;
    }

    private void NetworkChanged()
    {
        var key = owner.Networks.Current?.Key;
        if (key == observedNetwork) return;
        observedNetwork = key;
        if (!afterConnect) ModeChanged();
    }

    internal void Restore(bool enabled, bool defer)
    {
        active?.Cancel();
        owner.TunnelPolicy.Zapret = enabled;
        Tried = false; afterConnect = enabled && defer; modeChanged = false;
        repickAt = DateTimeOffset.MaxValue;
        if (!enabled)
        {
            stopWhenSafe |= status?.Running == true;
            domains = []; pendingNotice = null; Demoted = [];
            Watch(ZapretPlan.NoBypass, [], "");
        }
        Publish();
    }

    internal Task EnableAsync() => RunAsync(async token =>
    {
        owner.TunnelPolicy.Zapret = true; Tried = false; afterConnect = false; modeChanged = false;
        repickAt = DateTimeOffset.MaxValue;
        owner.Persistence.Queue();
        await PickCoreAsync(false, token);
    }, interrupt: true);

    internal Task DisableAsync() => RunAsync(async token =>
    {
        owner.TunnelPolicy.Zapret = false; afterConnect = false; modeChanged = false;
        repickAt = DateTimeOffset.MaxValue;
        owner.Persistence.Queue();
        domains = []; Demoted = []; pendingNotice = null;
        Watch(ZapretPlan.NoBypass, [], "");
        stopWhenSafe = true;
        SetNote("");
        await ReconcileAsync(token);
    }, interrupt: true);

    internal Task SetGroupAsync(string key, bool bypass) => RunAsync(async token =>
    {
        if (bypass) KeptInVpn.Remove(key); else KeptInVpn.Add(key);
        owner.Persistence.Queue();
        if (!Enabled) { Publish(); return; }
        if (bypass) { await PickCoreAsync(true, token); return; }
        var groups = ActiveGroups.Where(g => g != key).ToArray();
        Watch(ActiveStrategy, groups, activeNetwork);
        domains = ReadDomains(groups);
        stopWhenSafe |= groups.Length == 0 && bypassing;
        SetNote(ZapretPlan.Describe(ActiveStrategy, groups, null, KeptInVpn));
        await ReconcileAsync(token);
    }, interrupt: true);

    internal Task PickAsync() => RunAsync(token => PickCoreAsync(true, token));
    internal void CancelPick() => active?.Cancel();
    internal void ModeChanged()
    {
        if (!Enabled) return;
        active?.Cancel();
        modeChanged = true; Schedule();
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, bool interrupt = false)
    {
        if (disposed) return;
        if (interrupt) active?.Cancel();
        try { await commands.WaitAsync(shutdown.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (disposed) return;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            active = cancel;
            try { await work(cancel.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is UserError or IOException or TimeoutException or ObjectDisposedException)
            { if (!disposed) SetNote(error is UserError ? error.Message : "Обход не удалось обновить."); }
            finally { active = null; if (!disposed) Schedule(); }
        }
        finally { commands.Release(); }
    }

    private async Task ReconcileAsync(CancellationToken token)
    {
        if (RoutingPending)
        {
            nextRoutingAttempt = DateTimeOffset.UtcNow.AddSeconds(10);
            await owner.Rules.ApplyListsAsync(allowLeaseRebuild: false);
        }
        token.ThrowIfCancellationRequested();
        if (RoutingPending) { Publish(); return; }
        if (stopWhenSafe)
        {
            var stopped = await CallAsync(new(ZapretAction.Stop));
            stopWhenSafe = stopped is { Running: true } or { Error: not null };
            nextRoutingAttempt = DateTimeOffset.UtcNow.AddSeconds(10);
        }
        if (pendingNotice is { } message) { pendingNotice = null; BypassBroken?.Invoke(message); }
        Publish();
    }

    private string[] ReadDomains(string[] groups) =>
        ZapretSites.Read(tag, ZapretPlan.Groups.Where(g => groups.Contains(g.Key)).Select(g => g.List));

    private void SetNote(string text) { Note = text; Publish(); }
    private void Publish() { if (!disposed) { Changed?.Invoke(); Schedule(); } }

    private async Task<ZapretStatus?> CallAsync(ZapretRequest request)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            if (owner.Helper is { IsService: true } lifetime)
                return status = await lifetime.Client.ZapretAsync(request, timeout.Token);
            if (!NetworkHelperConnection.ServiceRunning) return status = null;
            await using var temporary = await ServiceHelperLifetime.ConnectAsync(NetworkServiceControl.Query().ProcessId, timeout.Token);
            return status = await temporary.Client.ZapretAsync(request, timeout.Token);
        }
        catch (Exception error) when (error is UserError or IOException or TimeoutException or OperationCanceledException or
            UnauthorizedAccessException or ObjectDisposedException or System.ComponentModel.Win32Exception)
        { return status = new(false, false, null, null, [], "Служба не ответила на команду zapret."); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; timer?.Dispose(); timer = null;
        owner.Networks.Changed -= NetworkChanged;
        shutdown.Cancel(); Changed = null; BypassBroken = null;
        if (!stopWhenSafe && active is null && status?.Running != true) return;
        // Служба живёт дольше окна и владельца. Остановка нужна только при выходе владельца.
        try { Task.Run(StopServiceAsync).Wait(TimeSpan.FromSeconds(4)); }
        catch (AggregateException) { }
    }

    private static async Task StopServiceAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await using var helper = await ServiceHelperLifetime.ConnectAsync(NetworkServiceControl.Query().ProcessId, timeout.Token).ConfigureAwait(false);
        await helper.Client.ZapretAsync(new(ZapretAction.Stop), timeout.Token).ConfigureAwait(false);
    }
}
