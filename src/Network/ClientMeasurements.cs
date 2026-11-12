namespace Oblachka;

internal interface IClientMeasurementBackend
{
    string? Network();
    bool Metered();
    Task PingAsync(IReadOnlyList<Profile> targets, Action<Profile, long?> result, CancellationToken token);
    Task MeasureAsync(IReadOnlyList<Profile> targets, bool physical, Action<ServerMeasurement> result,
        Func<Profile, Profile?> route, bool throughput, CancellationToken token, int parallelism = ServerMeasurements.Parallel, Action<Profile>? starting = null);
    Task MeasureManualAsync(IReadOnlyList<Profile> targets, SystemTunnelSession? session, Action<ServerMeasurement> result,
        bool speed, CancellationToken token, int parallelism = ServerMeasurements.Parallel);
}
internal sealed class ClientMeasurementBackend : IClientMeasurementBackend
{
    public string? Network() => NetworkIdentity.Current();
    public bool Metered() => NetworkCost.Metered();
    private static readonly Uri DefaultLatencyTarget = new("https://1.1.1.1/cdn-cgi/trace");
    private static Uri LatencyTarget => TunnelHealth.Custom ?? DefaultLatencyTarget;
    public Task PingAsync(IReadOnlyList<Profile> targets, Action<Profile, long?> result, CancellationToken token) => ServerPing.PingAllAsync(targets, result, token);
    public Task MeasureAsync(IReadOnlyList<Profile> targets, bool physical, Action<ServerMeasurement> result,
        Func<Profile, Profile?> route, bool throughput, CancellationToken token, int parallelism = ServerMeasurements.Parallel, Action<Profile>? starting = null) => ServerMeasurements.MeasureAsync(targets,
            () => new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), "") { Physical = physical }, result, token,
            route, throughput, parallelism, starting, LatencyTarget);

    public Task MeasureManualAsync(IReadOnlyList<Profile> targets, SystemTunnelSession? session, Action<ServerMeasurement> result,
        bool speed, CancellationToken token, int parallelism = ServerMeasurements.Parallel)
    {
        var tools = session is null ? Path.Combine(AppContext.BaseDirectory, "tools") : WfpProbeAccess.CoreDirectory;
        return ServerMeasurements.MeasureAsync(targets, () => new Engine(Path.Combine(tools, "xray.exe"), "") { Physical = session is not null },
            result, token, throughput: speed, parallelism: parallelism, latencyTarget: LatencyTarget,
            prepare: session is null ? null : session.PrepareProbeAsync);
    }
}

// Результаты и задачи принадлежат владельцу. Окно подписывается только на изменения.
public sealed partial class ClientMeasurements : IDisposable
{
    private readonly ClientRuntime owner;
    private readonly IClientMeasurementBackend backend;
    private readonly Func<DateTimeOffset> now;
    private CancellationTokenSource? measuring, pinging;
    private bool pingQueued, disposed;
    private int pingEpoch, measureEpoch;
    public bool IsMeasuring => measuring is not null;
    public bool Quiet { get; private set; }
    public string Message { get; private set; } = "";
    public DateTimeOffset LastPing { get; private set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastMeasure { get; private set; } = DateTimeOffset.MinValue;
    private DateTimeOffset lastThroughput = DateTimeOffset.MinValue;
    private string? throughputNetwork;
    public Task PingTask { get; private set; } = Task.CompletedTask;
    public Task MeasureTask { get; private set; } = Task.CompletedTask;
    public event Action? Changed;
    internal ClientMeasurements(ClientRuntime owner, IClientMeasurementBackend? backend = null, Func<DateTimeOffset>? now = null)
    {
        this.owner = owner; this.backend = backend ?? new ClientMeasurementBackend(); this.now = now ?? (() => DateTimeOffset.UtcNow);
        owner.Imports.Applied += Imported;
    }
    private void Imported(ClientImportChange change) { if (Enabled) { QueuePing(); Schedule(TimeSpan.FromSeconds(1)); } }

    public bool CanMeasure => !disposed && (owner.Workspace.Measurements.AllowConnected ||
        owner.Tunnel is null && owner.Engine.State is ConnectionState.Idle or ConnectionState.Failed && !owner.Connections.IsActive);
    private bool CanPing => !disposed && Enabled && !owner.Workspace.Measurements.ManualOnly && CanMeasure && owner.Tunnel is null && !(owner.Preferences.SystemTunnel && owner.Activity.EngineOperation) &&
        !owner.Activity.VmTestActive && owner.Profiles.Imported.Count > 0;
    public void QueuePing()
    {
        if (pinging is not null) { pingQueued = true; return; }
        if (CanPing) PingTask = PingAsync();
    }
    public async Task WaitForPingsAsync()
    {
        if (pinging is null && owner.Profiles.Pings.Count > 0) return;
        if (pinging is null) QueuePing();
        await Task.WhenAny(PingTask, Task.Delay(TimeSpan.FromSeconds(3)));
    }
    private async Task PingAsync()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20)); pinging = cancel; pingQueued = false;
        var epoch = ++pingEpoch; var targets = owner.Profiles.Imported.ToArray();
        try
        {
            await backend.PingAsync(targets, (profile, milliseconds) => Dispatch(() =>
            {
                if (!CanPing || cancel.IsCancellationRequested || epoch != pingEpoch || !owner.Profiles.Imported.Any(p => Profiles.Fingerprint(p) == Profiles.Fingerprint(profile))) return;
                var key = Profiles.Fingerprint(profile);
                owner.Profiles.Pings[key] = new(key, now(), milliseconds is not null, milliseconds ?? 0) { Handshake = true };
                Changed?.Invoke();
            }), cancel.Token);
        }
        catch (OperationCanceledException) { }
        finally
        {
            LastPing = now(); pinging = null; owner.Profiles.TrimMeasurements();
            if (pingQueued && !disposed) QueuePing();
            if (!disposed) Schedule(NextDelay());
        }
    }
    public Task MeasureAsync(IReadOnlyList<Profile> targets, bool quiet, CancellationToken token = default, bool speed = false)
    {
        if (disposed || IsMeasuring || targets.Count == 0) return Task.CompletedTask;
        if (!CanMeasure) { Quiet = quiet; Message = "Проверка серверов доступна при выключенном VPN. Политику можно изменить в настройках автовыбора."; Changed?.Invoke(); return Task.CompletedTask; }
        return MeasureTask = MeasureCoreAsync(targets.ToArray(), quiet, token, false, null, speed);
    }
    private async Task MeasureCoreAsync(IReadOnlyList<Profile> targets, bool quiet, CancellationToken token, bool background,
        Action<Profile>? starting, bool speedOnly = false)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token); measuring = cancel; Quiet = quiet; LastMeasure = now();
        if (background) cancel.CancelAfter(TimeSpan.FromSeconds(30));
        else { nextMeasurement = now().AddMinutes(10); backgroundQueue = null; }
        var epoch = ++measureEpoch; var network = backend.Network(); var session = owner.Tunnel;
        Profile? Route(Profile p) => session is null ? p : session.PinnedFor(p);
        var throughput = background && owner.Workspace.Measurements.BackgroundSpeed && (network != throughputNetwork || now() - lastThroughput > TimeSpan.FromHours(3)) && !backend.Metered();
        var done = 0; var speedDone = 0; var speedFailed = 0;
        var skipped = background ? targets.Count(t => Route(t) is null) : 0;
        void Measured(ServerMeasurement result, bool speed)
        {
            if (disposed || cancel.IsCancellationRequested || epoch != measureEpoch || !ReferenceEquals(session, owner.Tunnel)) return;
            if (speedOnly && (!result.Success || result.Throughput <= 0))
            {
                speedFailed++;
                return;
            }
            if (result is { Success: true, Throughput: 0 } && owner.Profiles.Measurements.TryGetValue(result.Fingerprint, out var old) && old.Throughput > 0)
                result = result with { Throughput = old.Throughput };
            owner.Profiles.Measurements[result.Fingerprint] = result;
            Message = speed ? $"Замер скорости: {++speedDone}. Можно остановить." : $"Проверка задержки: {++done} из {targets.Count - skipped}. Можно остановить.";
            Changed?.Invoke();
        }
        Changed?.Invoke();
        try
        {
            if (background)
                await backend.MeasureAsync(targets, session is not null, r => Measured(r, false), Route, false, cancel.Token, 1, starting);
            else
                await backend.MeasureManualAsync(targets, session, r => Measured(r, speedOnly), speedOnly, cancel.Token, owner.Workspace.Measurements.Normalize().Parallelism);
            cancel.Token.ThrowIfCancellationRequested();
            if (throughput && ReferenceEquals(session, owner.Tunnel))
            {
                var fastest = ServerMeasurements.ThroughputTargets(targets, owner.Profiles.Measurements, now(), owner.Profiles.Running ?? owner.Profiles.Selected);
                if (fastest.Count > 0) { lastThroughput = now(); throughputNetwork = network; }
                await backend.MeasureAsync(fastest, session is not null, r => Measured(r, true), Route, true, cancel.Token, 1);
            }
            Message = speedOnly ? (speedFailed > 0 ? "Не удалось измерить скорость." : "Замер скорости завершён.") : "Проверка задержки завершена." +
                (skipped > 0 ? $" Пропущено серверов вне текущей сессии: {skipped}." : "");
        }
        catch (OperationCanceledException) { Message = "Замер остановлен. Полученные результаты сохранены."; }
        catch (Exception error) when (error is UserError or IOException or TimeoutException) { Message = "Замер не завершён. Полученные результаты сохранены."; }
        finally
        {
            measuring = null; owner.Profiles.TrimMeasurements(); owner.Persistence.Queue();
            if (!disposed) { Changed?.Invoke(); Schedule(NextDelay()); }
        }
    }
    public void CancelMeasure() { measureEpoch++; measuring?.Cancel(); }
    public void InvalidateNetwork()
    {
        pingEpoch++; measureEpoch++; pinging?.Cancel(); measuring?.Cancel();
        owner.Profiles.Pings.Clear();
        owner.Profiles.Measurements.Clear();
        LastMeasure = DateTimeOffset.MinValue; nextMeasurement = now().AddSeconds(15); backgroundQueue = null; scheduleEpoch++;
        Changed?.Invoke();
        Schedule(TimeSpan.FromSeconds(15));
    }
    public void PrepareTunnel() { pingEpoch++; pinging?.Cancel(); CancelMeasure(); }
    private void Dispatch(Action action)
    { if (context is null) { if (!disposed) action(); } else context.Post(_ => { if (!disposed) action(); }, null); }
    public void Dispose()
    {
        if (disposed) return; disposed = true; StopSchedule(); pingEpoch++; measureEpoch++;
        pinging?.Cancel(); measuring?.Cancel(); owner.Imports.Applied -= Imported; Changed = null;
    }
}
