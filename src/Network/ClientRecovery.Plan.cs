namespace Oblachka;

public sealed partial class ClientRecovery
{
    private sealed record SearchPlan(Profile[] Subset, int[] Candidates, int[] Order, LadderStep[] Siblings, int[] Outside);
    private SearchPlan BuildPlan(Profile previous)
    {
        var data = owner.Profiles; var all = data.Imported.ToArray();
        int[] candidates; int start;
        if (data.AutoSelect)
        {
            data.MarkMeasured(previous, null);
            candidates = RouteCandidates.ByLatency(RouteCandidates.For(all, previous, anyNode: true, data.InAutoPool),
                all, data.LatestMeasurement, failed: previous); start = 0;
        }
        else
        {
            candidates = RouteCandidates.For(all, previous, pool: data.InAutoPool, machine: data.MachineMode);
            start = RouteCandidates.StartAt(candidates, all, previous);
        }
        var subset = candidates.Select(i => all[i]).ToArray();
        if (subset.Length == 0) throw new UserError("Нет доступных серверов для восстановления. Выберите сервер и подключитесь вручную.");
        var order = new[] { start }.Concat(Enumerable.Range(0, subset.Length).Where(i => i != start)).ToArray();
        var machine = ServerMachines.Key(previous);
        var same = data.MachineMode ? order.Where(i => i != start && ServerMachines.Key(subset[i]) == machine).ToArray() : [];
        bool InSession(int i) => !TunnelActive || tunnel()!.CanSwitchTo(subset[i]);
        return new(subset, candidates, order, same.Where(InSession).Select(i => new LadderStep(i, false)).ToArray(),
            same.Where(i => !InSession(i)).ToArray());
    }
    private RouteSearch CreateSearch()
    {
        var core = owner.Engine;
        void Fragment(bool cut) { if (core is Engine engine) engine.Fragment = cut; }
        return TunnelActive && tunnel() is { } session
            ? new(session.SwitchAsync, core.ProbeAsync, static () => { }, Fragment)
            : new(core, Fragment);
    }
    private async Task<Diagnosis> DiagnoseAsync(Profile failed, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            return await diagnose(failed, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(Blockage.Unknown, "Проверка сервера не уложилась во время."); }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException) { return new(Blockage.Unknown, ""); }
    }
}
