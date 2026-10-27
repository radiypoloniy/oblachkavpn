namespace Oblachka;

public interface INetworkHelperClient : IAsyncDisposable
{
    event Action? ConnectionLost;
    // Служба заметила, что защиту сняли без нас, и поставила её заново.
    event Action? ProtectionRepaired;
    // Возвращает признак того, блокируется ли трафик мимо туннеля.
    Task<bool> AcquireAsync(NetworkLeaseRequest request, CancellationToken token);
    Task<NetworkSessionState> ReleaseAsync(CancellationToken token);
}

// Maintains the lease independently of UI timers. Losing the pipe causes server-side recovery.
public sealed class NetworkHelperClient : INetworkHelperClient
{
    private readonly HelperPipeClient pipe;
    private readonly SemaphoreSlim operations = new(1);
    private readonly TimeSpan interval;
    private Guid? lease;
    private CancellationTokenSource? pulseCancellation;
    private Task? pulse;
    private bool disposed;
    public event Action? ConnectionLost;
    public event Action? ProtectionRepaired;
    public NetworkHelperClient(HelperPipeClient pipe, TimeSpan? interval = null)
    {
        this.pipe = pipe; this.interval = interval ?? TimeSpan.FromSeconds(3);
        if (this.interval <= TimeSpan.Zero || this.interval > TimeSpan.FromSeconds(5)) throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public async Task<bool> AcquireAsync(NetworkLeaseRequest request, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (lease is not null) throw new UserError("Сетевая сессия уже запущена.");
            if (request.Action != NetworkLeaseAction.Acquire) throw new UserError("Требуется команда Acquire.");
            NetworkHelperContracts.Validate(request);
            lease = request.LeaseId; // A lost acknowledgement must not be mistaken for a released lease.
            var response = await pipe.SendAsync(HelperOperation.Acquire, request, token).ConfigureAwait(false);
            if (response.Failure != HelperFailure.None || response.State != NetworkSessionState.Applied)
                // Причина называется: без неё обрыв и отказ системы выглядят одинаково.
                throw new UserError("Helper не подтвердил запуск сетевой сессии: " +
                    response.Failure + ", состояние " + (response.State?.ToString() ?? "нет") + ".");
            pulseCancellation = new CancellationTokenSource();
            pulse = PulseAsync(pulseCancellation.Token);
            // Отсутствие признака трактуется как «не блокируется»: обещать защиту
            // по умолчанию нельзя.
            return response.Blocking == true;
        }
        finally { operations.Release(); }
    }

    public async Task<NetworkSessionState> ReleaseAsync(CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try { return await ReleaseCoreAsync(token).ConfigureAwait(false); }
        finally { operations.Release(); }
    }

    private async Task<NetworkSessionState> ReleaseCoreAsync(CancellationToken token)
    {
        pulseCancellation?.Cancel();
        if (pulse is not null) await pulse.ConfigureAwait(false);
        pulseCancellation?.Dispose(); pulseCancellation = null; pulse = null;
        if (lease is null) return NetworkSessionState.Released;
        var response = await pipe.SendAsync(HelperOperation.Release,
            new(1, lease.Value, NetworkLeaseAction.Release, 0, Array.Empty<string>()), token).ConfigureAwait(false);
        if (response.State == NetworkSessionState.RecoveryRequired) return NetworkSessionState.RecoveryRequired;
        if (response.Failure != HelperFailure.None || response.State != NetworkSessionState.Released)
            throw new UserError("Helper не подтвердил восстановление сети.");
        lease = null;
        return NetworkSessionState.Released;
    }

    // Исправление сети Windows службой; true — исправлено, false — поломки уже нет.
    public async Task<bool> RepairAsync(RepairRequest repair, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var response = await pipe.SendAsync(HelperOperation.Repair, null, token, repair).ConfigureAwait(false);
            if (response.Failure == HelperFailure.InvalidRequest) throw new UserError("Служба отказалась: " + repair.Kind + ".");
            if (response.Failure != HelperFailure.None) throw new UserError("Windows не дала выполнить исправление.");
            return response.Blocking == true;
        }
        finally { operations.Release(); }
    }

    // «Без VPN — без интернета»: служба ставит или снимает постоянный запрет; true — запрет стоит.
    public async Task<bool> LockdownAsync(bool on, bool lan, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var response = await pipe.SendAsync(HelperOperation.Lockdown, null, token, lockdown: new(on, lan)).ConfigureAwait(false);
            if (response.Failure != HelperFailure.None) throw new UserError("Служба не смогла " + (on ? "закрыть" : "открыть") + " интернет без VPN.");
            return response.Blocking == true;
        }
        finally { operations.Release(); }
    }

    // Сайты, которые идут мимо VPN: их имена разрешает DNS физической сети (DirectDns).
    public async Task DirectDnsAsync(IReadOnlyList<string> domains, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var part in DirectDnsRequest.Split(domains))
                if ((await pipe.SendAsync(HelperOperation.DirectDns, null, token, directDns: part).ConfigureAwait(false)).Failure != HelperFailure.None)
                    throw new UserError("Служба не приняла список прямых сайтов.");
        }
        finally { operations.Release(); }
    }

    // Списки маршрутизации для ядра туннеля (RoutingLists): до запуска сессии и на ходу.
    public async Task RoutingListsAsync(CompiledRouting routing, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var part in RoutingListsRequest.Split(routing))
                if ((await pipe.SendAsync(HelperOperation.RoutingLists, null, token, lists: part).ConfigureAwait(false)).Failure != HelperFailure.None)
                    throw new UserError("Служба не приняла списки маршрутизации.");
        }
        finally { operations.Release(); }
    }

    // Правила программ для работающего туннеля: ядро перечитывает их без переподключения.
    public async Task ProgramRulesAsync(IReadOnlyList<ProgramRule> rules, IReadOnlyList<SiteRule> sites, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if ((await pipe.SendAsync(HelperOperation.ProgramRules, null, token, programs: new([.. rules], [.. sites])).ConfigureAwait(false)).Failure != HelperFailure.None)
                throw new UserError("Служба не приняла правила программ.");
        }
        finally { operations.Release(); }
    }

    // zapret в службе: подготовка, запуск, остановка, состояние. Отказ с причиной — в Error.
    public async Task<ZapretStatus> ZapretAsync(ZapretRequest request, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var response = await pipe.SendAsync(HelperOperation.Zapret, null, token, zapret: request).ConfigureAwait(false);
            if (response.Failure != HelperFailure.None || response.Zapret is null) throw new UserError("Служба не выполнила команду zapret.");
            return response.Zapret;
        }
        finally { operations.Release(); }
    }

    // Снять сессию, оставленную прежним владельцем (окно завершилось аварийно при службе).
    public async Task<NetworkSessionState> RecoverAsync(CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (lease is not null) throw new UserError("Сначала остановите своё подключение.");
            var response = await pipe.SendAsync(HelperOperation.Recover, null, token).ConfigureAwait(false);
            return response.State ?? NetworkSessionState.Released;
        }
        finally { operations.Release(); }
    }

    private async Task PulseAsync(CancellationToken token)
    {
        // Счётчик восстановлений службы с её запуска: первый пульс задаёт отсчёт, рост — событие.
        var repairs = -1;
        try
        {
            while (true)
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
                var response = await pipe.SendAsync(HelperOperation.Heartbeat, null, CancellationToken.None).ConfigureAwait(false);
                if (response.Failure != HelperFailure.None || response.State != NetworkSessionState.Applied)
                    throw new UserError("Сетевая сессия больше не подтверждена helper.");
                if (response.Repairs is int count)
                {
                    if (repairs >= 0 && count > repairs) _ = Task.Run(() => ProtectionRepaired?.Invoke());
                    repairs = count;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UserError or OperationCanceledException or ObjectDisposedException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            // Keep callbacks off the heartbeat task: a subscriber may synchronously start cleanup.
            _ = Task.Run(() => ConnectionLost?.Invoke());
        }
    }

    public async ValueTask DisposeAsync()
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            try { await ReleaseCoreAsync(CancellationToken.None).ConfigureAwait(false); }
            finally { await pipe.DisposeAsync().ConfigureAwait(false); }
        }
        finally { operations.Release(); }
    }
}
