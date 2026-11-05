namespace Oblachka;

public sealed partial class SystemTunnelSession
{
    internal IReadOnlyList<PinnedServiceRoute> PinnedServices { get; private set; } = [];

    internal TrafficSource? TrafficSourceAt(int index)
    {
        var candidates = plans;
        var exits = direct;
        if (index < 0 || index >= candidates.Count || exits is null || index >= exits.Length || exits[index] is null) return null;
        var profile = candidates[index].Source;
        return new(ClientServerCatalog.Id(profile)!, ClientServerCatalog.Title(profile));
    }

    private async Task StartCoreWithStatsAsync(TunnelEndpointPlan plan, CoreDirectEntry? coreDirect, CancellationToken token)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            if (Stats!.Port == Tuning?.LanPort || Stats.ProgramsPort == Tuning?.LanPort || Stats.Port == coreDirect?.Port || Stats.ProgramsPort == coreDirect?.Port)
            { Stats = CoreProgramPorts.Create(); continue; }
            core.CarriedBy = Carries(Choose(plan)) ? Stats : null;
            await core.StartAsync(plan.PinnedProfile, token).ConfigureAwait(false);
            leasePort = core.Port;
            if (leasePort != Stats.Port && leasePort != Stats.ProgramsPort) return;
            // Recreate the carried controller with the new secret as well. Merely
            // replacing Stats would leave its health checks pointing at the old API.
            core.Stop();
            Stats = CoreProgramPorts.Create();
        }
        throw new UserError("Не удалось согласовать локальные порты ядра.");
    }
    internal async Task SetMainPausedAsync(bool paused, bool lockdown)
    {
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!CanPauseMain || State is not (SystemTunnelState.Running or SystemTunnelState.Holding) || Stats is not { } stats)
                throw new UserError("Остановка основного VPN со строгой защитой доступна для привязок по точному пути к программе.");
            var existing = await CoreConnections.ReadAsync(stats, CancellationToken.None).ConfigureAwait(false)
                ?? throw new UserError("Ядро не подтвердило список активных соединений.");
            await CoreSelector.SetModeAsync(stats, paused
                ? lockdown ? SingBoxTunConfig.MainBlocked : SingBoxTunConfig.MainOff
                : SingBoxTunConfig.MainOn).ConfigureAwait(false);
            MainPaused = paused;
            var pins = PinnedServices.Select(r => r.ExitIndex).OfType<int>().Select(SingBoxTunConfig.DirectTag).ToHashSet();
            Changed?.Invoke(State);
            bool IsPinned(CoreConnection c) => c.Chains.Count == 1 && pins.Contains(c.Chains[0]);
            var ordinary = existing.Where(c => !IsPinned(c)).Select(c => c.Id).OfType<string>().ToHashSet();
            bool MustClose(CoreConnection c) => !IsPinned(c) && paused &&
                (lockdown || c.Chains.Contains(SingBoxTunConfig.ServerSelector) || c.Chains.Contains("local-core"));
            var after = await CoreConnections.ReadAsync(stats, CancellationToken.None).ConfigureAwait(false);
            if (after is not null) ordinary.UnionWith(after.Where(MustClose).Select(c => c.Id).OfType<string>());
            if (after is not null && ordinary.Count == 0) return;
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await CoreConnections.CloseIdsAsync(stats, ordinary, closeTimeout.Token).ConfigureAwait(false);
            var remaining = await CoreConnections.ReadAsync(stats, CancellationToken.None).ConfigureAwait(false);
            if (remaining is null || remaining.Any(c => MustClose(c) || c.Id is { } id && ordinary.Contains(id)))
            {
                if (paused && lockdown)
                {
                    await CleanupAsync().ConfigureAwait(false);
                    if (State == SystemTunnelState.Idle) Set(SystemTunnelState.Failed);
                }
                throw new UserError("Основной выход переключён. Ядро не подтвердило закрытие прежних соединений.");
            }
        }
        finally { operations.Release(); }
    }

}
