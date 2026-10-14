namespace Oblachka;

// Holding — ядро остановилось или переключается, а туннель и блокировка остаются на месте.
public enum SystemTunnelState { Idle, Starting, Running, Holding, Stopping, RecoveryRequired, Failed }

// Порядок системного режима. Снять сетевую сессию вправе только явная остановка.
//
// Сбой ядра — не команда «вернуть обычный интернет». Раньше любое падение ядра вело к
// снятию сессии у помощника, то есть блокировка исчезала ровно в тот момент, когда она
// нужнее всего. Теперь при сбое ядра туннель и блокировка остаются, сессия переходит в
// Holding, и ядро перезапускается на том же локальном порту, куда смотрит туннель.
//
// Другой узел можно выбрать без снятия блокировки, только если его адреса разрешены при
// запуске: помощник принимает список узлов один раз. Поэтому план сессии — это набор
// заранее подготовленных узлов, а не один.
//
// Потеря помощника — другое дело: сетевую сессию он восстанавливает сам, на своей стороне,
// и блокировки после этого нет. Об этом сообщает Failed, а не Holding.
public sealed class SystemTunnelSession : IAsyncDisposable
{
    private readonly ITunnelController core;
    private readonly INetworkHelperClient helper;
    private readonly SemaphoreSlim operations = new(1);
    private IReadOnlyList<TunnelEndpointPlan> plans = Array.Empty<TunnelEndpointPlan>();
    private TunnelEndpointPlan? current;
    private int leasePort;
    public SystemTunnelState State { get; private set; }
    // Блокируется ли трафик мимо туннеля. Решает помощник, окно только сообщает.
    public bool BlocksBypass { get; private set; }
    public event Action<SystemTunnelState>? Changed;

    public SystemTunnelSession(ITunnelController core, INetworkHelperClient helper)
    {
        this.core = core; this.helper = helper;
        helper.ConnectionLost += OnHelperLost;
        core.Changed += CoreChanged;
    }

    // Узлы, на которые можно переключиться без снятия блокировки.
    public bool CanSwitchTo(Profile profile) => Find(profile) is not null;

    private void CoreChanged(ConnectionState state, string message) { if (state == ConnectionState.Failed) _ = CoreLostAsync(); }

    private async Task CoreLostAsync()
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            // Событие могло устареть, пока ждало очереди: ядро уже перезапущено.
            if (State == SystemTunnelState.Running && core.State == ConnectionState.Failed) Set(SystemTunnelState.Holding);
        }
        finally { operations.Release(); }
    }

    private void OnHelperLost() => _ = HelperLostAsync();
    private async Task HelperLostAsync()
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State is not (SystemTunnelState.Running or SystemTunnelState.Starting or SystemTunnelState.Holding)) return;
            try { await CleanupAsync().ConfigureAwait(false); }
            catch { Set(SystemTunnelState.RecoveryRequired); }
            if (State == SystemTunnelState.Idle) Set(SystemTunnelState.Failed);
        }
        finally { operations.Release(); }
    }

    public Task StartAsync(TunnelEndpointPlan plan, CancellationToken token) => StartWithSparesAsync(new[] { plan }, token);

    // Первый план запускается, остальные только разрешаются: их адреса входят в аренду.
    public async Task StartWithSparesAsync(IReadOnlyList<TunnelEndpointPlan> prepared, CancellationToken token,
        IReadOnlyList<SiteRule>? sites = null, IReadOnlyList<ProgramRule>? programs = null)
    {
        if (prepared.Count == 0) throw new UserError("Нет подготовленного сервера для системного режима.");
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (State != SystemTunnelState.Idle) throw new UserError("Сначала остановите системный туннель.");
            Set(SystemTunnelState.Starting);
            try
            {
                plans = prepared.ToArray(); current = plans[0];
                var peers = plans.SelectMany(p => p.Peers).Distinct(StringComparer.Ordinal).ToArray();
                await core.StartAsync(current.PinnedProfile, token).ConfigureAwait(false);
                leasePort = core.Port;
                // Запрос к помощнику не прерывается отменой: отмена посреди него закрывает канал,
                // служба считает окно погибшим и держит сеть закрытой, а Release уже не дойдёт.
                // Запрос ограничен своим сроком в канале; отмена проверяется после него, и
                // сессия снимается обычным Release.
                BlocksBypass = await helper.AcquireAsync(new(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, leasePort, peers,
                    sites?.ToArray(), programs?.ToArray()), CancellationToken.None).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Set(SystemTunnelState.Running);
            }
            catch
            {
                try { await CleanupAsync().ConfigureAwait(false); }
                catch { Set(SystemTunnelState.RecoveryRequired); }
                throw;
            }
        }
        finally { operations.Release(); }
    }

    // Перезапуск ядра под защитой: на тот же узел (после сбоя) или на другой разрешённый.
    // Туннель смотрит на локальный порт ядра, а ядро занимает прежний порт заново, поэтому
    // помощника это не касается. Неудача оставляет Holding: блокировка продолжает действовать.
    public async Task SwitchAsync(Profile? profile, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (State is not (SystemTunnelState.Running or SystemTunnelState.Holding))
                throw new UserError(State == SystemTunnelState.RecoveryRequired
                    ? "Системный режим остановлен не полностью. Нажмите «Остановить», затем подключитесь снова."
                    : "Системный режим не запущен.");
            var plan = profile is null ? current : Find(profile);
            if (plan is null)
                throw new UserError("Этот сервер не был разрешён при включении системного режима. " +
                    "Остановите подключение и запустите его снова на нужном сервере.");
            Set(SystemTunnelState.Holding);
            core.Stop();
            await core.StartAsync(plan.PinnedProfile, token).ConfigureAwait(false);
            if (core.Port != leasePort)
            {
                // Туннель указывает на прежний порт. Ядро на другом порту ничего бы не несло.
                core.Stop();
                throw new UserError("Ядро не смогло занять прежний локальный порт. Остановите подключение и запустите его снова.");
            }
            current = plan;
            Set(SystemTunnelState.Running);
        }
        finally { operations.Release(); }
    }

    public async Task StopAsync()
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try { await CleanupAsync().ConfigureAwait(false); }
        finally { operations.Release(); }
    }

    private TunnelEndpointPlan? Find(Profile profile)
    {
        var sources = plans.Select(p => p.Source).ToArray();
        var index = ProfileSelection.IndexOf(sources, profile);
        return index >= 0 ? plans[index] : null;
    }

    private async Task CleanupAsync()
    {
        Set(SystemTunnelState.Stopping);
        BlocksBypass = false;
        var recovered = false;
        try
        {
            var state = await helper.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
            recovered = state == NetworkSessionState.Released;
        }
        catch { Set(SystemTunnelState.RecoveryRequired); throw; }
        finally { core.Stop(); }
        Set(recovered ? SystemTunnelState.Idle : SystemTunnelState.RecoveryRequired);
    }

    private void Set(SystemTunnelState state) { State = state; Changed?.Invoke(state); }

    public async ValueTask DisposeAsync()
    {
        helper.ConnectionLost -= OnHelperLost; core.Changed -= CoreChanged;
        try { await StopAsync().ConfigureAwait(false); }
        finally { core.Dispose(); await helper.DisposeAsync().ConfigureAwait(false); }
    }
}
