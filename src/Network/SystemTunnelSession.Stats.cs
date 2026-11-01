namespace Oblachka;

public sealed partial class SystemTunnelSession
{
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
}
