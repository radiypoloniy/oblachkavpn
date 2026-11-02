namespace Oblachka;

internal interface IClientMeasurementBackend
{
    string? Network();
    bool Metered();
    Task PingAsync(IReadOnlyList<Profile> targets, Action<Profile, long?> result, CancellationToken token);
    Task MeasureAsync(IReadOnlyList<Profile> targets, bool physical, Action<ServerMeasurement> result,
        Func<Profile, Profile?> route, bool throughput, CancellationToken token);
}
internal sealed class ClientMeasurementBackend : IClientMeasurementBackend
{
    public string? Network() => NetworkIdentity.Current();
    public bool Metered() => NetworkCost.Metered();
    public Task PingAsync(IReadOnlyList<Profile> targets, Action<Profile, long?> result, CancellationToken token) => ServerPing.PingAllAsync(targets, result, token);
    public Task MeasureAsync(IReadOnlyList<Profile> targets, bool physical, Action<ServerMeasurement> result,
        Func<Profile, Profile?> route, bool throughput, CancellationToken token) => ServerMeasurements.MeasureAsync(targets,
            () => new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), "") { Physical = physical }, result, token, route, throughput);
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

    private bool CanPing => !disposed && Enabled && owner.Tunnel is null && !(owner.Preferences.SystemTunnel && owner.Activity.EngineOperation) &&
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
        }
    }
    public Task MeasureAsync(IReadOnlyList<Profile> targets, bool quiet, CancellationToken token = default)
    {
        if (disposed || IsMeasuring || targets.Count == 0) return Task.CompletedTask;
        return MeasureTask = MeasureCoreAsync(targets.ToArray(), quiet, token);
    }
    private async Task MeasureCoreAsync(IReadOnlyList<Profile> targets, bool quiet, CancellationToken token)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token); measuring = cancel; Quiet = quiet; LastMeasure = now();
        var epoch = ++measureEpoch; var network = backend.Network(); var session = owner.Tunnel;
        Profile? Route(Profile p) => session is null ? p : session.PinnedFor(p);
        var throughput = !quiet || (network != throughputNetwork || now() - lastThroughput > TimeSpan.FromHours(3)) && !backend.Metered();
        if (throughput) { lastThroughput = now(); throughputNetwork = network; }
        var done = 0; var speedDone = 0;
        var skipped = targets.Count(t => Route(t) is null);
        void Measured(ServerMeasurement result, bool speed)
        {
            if (disposed || cancel.IsCancellationRequested || epoch != measureEpoch || !ReferenceEquals(session, owner.Tunnel)) return;
            if (result is { Success: true, Throughput: 0 } && owner.Profiles.Measurements.TryGetValue(result.Fingerprint, out var old) && old.Throughput > 0)
                result = result with { Throughput = old.Throughput };
            owner.Profiles.Measurements[result.Fingerprint] = result;
            Message = speed ? $"Скорость лучших: {++speedDone}. Можно остановить." : $"Пинг: {++done} из {targets.Count - skipped}. Можно остановить.";
            Changed?.Invoke();
        }
        Changed?.Invoke();
        try
        {
            await backend.MeasureAsync(targets, session is not null, r => Measured(r, false), Route, false, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (throughput && ReferenceEquals(session, owner.Tunnel))
            {
                var fastest = ServerMeasurements.ThroughputTargets(targets, owner.Profiles.Measurements, now(), owner.Profiles.Running ?? owner.Profiles.Selected);
                await backend.MeasureAsync(fastest, session is not null, r => Measured(r, true), Route, true, cancel.Token);
            }
            Message = "Пинг обновлён: задержка через каждый сервер." + (skipped > 0 ? $" {skipped} не проверить, пока включён «Весь компьютер»: их адреса закрыты защитой." : "");
        }
        catch (OperationCanceledException) { Message = "Замер остановлен. Полученные результаты сохранены."; }
        catch (Exception error) when (error is UserError or IOException or TimeoutException) { Message = "Замер не завершён. Полученные результаты сохранены."; }
        finally
        {
            measuring = null; owner.Profiles.TrimMeasurements(); owner.Persistence.Queue();
            if (!disposed) Changed?.Invoke();
        }
    }
    public void CancelMeasure() { measureEpoch++; measuring?.Cancel(); }
    public void InvalidateNetwork()
    {
        pingEpoch++; measureEpoch++; pinging?.Cancel(); measuring?.Cancel();
        LastMeasure = DateTimeOffset.MinValue;
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
