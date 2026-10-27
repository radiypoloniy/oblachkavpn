namespace Oblachka;

public static partial class SelfTests
{
    private static async Task HealthChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Health requires three failed rounds across independent targets", async () =>
        {
            var health = new TunnelHealth(); var calls = 0;
            Task<ProbeResult> Fail(Uri _, CancellationToken ct) { ct.ThrowIfCancellationRequested(); calls++; return Task.FromResult(new ProbeResult(false, 1, "fixture", ProbeFailure.Reachability)); }
            for (var i = 0; i < 3; i++) { await health.CheckAsync(Fail, TunnelHealth.Targets, CancellationToken.None); Assert(health.ShouldRecover == (i == 2)); }
            Assert(calls == 6);
            await health.CheckAsync((_, _) => Task.FromResult(new ProbeResult(false, 1, "HTTP 503", ProbeFailure.TargetResponse)), TunnelHealth.Targets, CancellationToken.None);
            Assert(!health.ShouldRecover && health.FailedRounds == 0);
        });
        await check("Second reachable target prevents failover; cancellation does not count", async () =>
        {
            var health = new TunnelHealth(); var n = 0;
            var round = await health.CheckAsync((_, _) => Task.FromResult(++n == 1 ? new ProbeResult(false, 2, "fixture", ProbeFailure.Reachability) : new ProbeResult(true, 3, "fixture")), TunnelHealth.Targets, CancellationToken.None);
            Assert(round.Reachable && round.Verified && health.FailedRounds == 0);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            try { await health.CheckAsync((_, _) => Task.FromResult(new ProbeResult(false, 0, "fixture")), TunnelHealth.Targets, stop.Token); throw new Exception("Cancellation expected"); }
            catch (OperationCanceledException) { Assert(health.FailedRounds == 0); }
        });
        await check("Health asks both sites at once: a silent server fails within the budget, the first answer cancels the other", async () =>
        {
            var health = new TunnelHealth();
            static async Task<ProbeResult> Silent(Uri _, CancellationToken ct) { await Task.Delay(Timeout.Infinite, ct); return new(true, 0, "never"); }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var round = await health.CheckAsync(Silent, TunnelHealth.Targets, CancellationToken.None, TimeSpan.FromMilliseconds(300));
            Assert(!round.Reachable && health.FailedRounds == 1 && round.Results.Count == 2 && watch.ElapsedMilliseconds < 3000);
            var cancelled = false;
            async Task<ProbeResult> FirstWins(Uri target, CancellationToken ct)
            {
                if (target == TunnelHealth.Targets[1]) return new(true, 5, "fixture");
                try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { cancelled = true; throw; }
                return new(false, 0, "never");
            }
            round = await health.CheckAsync(FirstWins, TunnelHealth.Targets, CancellationToken.None);
            Assert(round.Reachable && round.Verified && cancelled && health.FailedRounds == 0);
        });
    }
}
