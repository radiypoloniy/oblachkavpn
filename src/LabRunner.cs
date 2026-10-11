using System.Diagnostics;

namespace RoutePilot;

public sealed record LabAttempt(int ProfileIndex, string Core, int Repetition, string Outcome, long StartupMilliseconds, long RequestMilliseconds);
public sealed record LabSummary(int ProfileIndex, int Attempts, int Successes, double SuccessRate, long? MedianRequestMilliseconds);
public sealed record LabResult(bool Canceled, IReadOnlyList<LabAttempt> Attempts, IReadOnlyList<LabSummary> Summary);

// Explicit serial experiments. Each sample owns a fresh process; it always stops it.
public sealed class LabRunner
{
    private readonly Func<Profile, CancellationToken, Task> start;
    private readonly Func<Uri, CancellationToken, Task<ProbeResult>> probe;
    private readonly Action stop;

    public LabRunner(Engine engine) : this(engine.StartAsync, engine.ProbeAsync, engine.Stop) { }
    internal LabRunner(Func<Profile, CancellationToken, Task> start,
        Func<Uri, CancellationToken, Task<ProbeResult>> probe, Action stop)
    { this.start = start; this.probe = probe; this.stop = stop; }

    public async Task<LabResult> RunAsync(LabPlan plan, CancellationToken token)
    {
        var attempts = new List<LabAttempt>();
        var canceled = false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            // Interleave profiles to reduce bias from testing one at a very different time.
            for (var repetition = 0; repetition < plan.Repetitions; repetition++)
            for (var index = 0; index < plan.Profiles.Count; index++)
            {
                budget.Token.ThrowIfCancellationRequested();
                var profile = plan.Profiles[index];
                var timer = Stopwatch.StartNew();
                long startup = 0;
                try
                {
                    stop();
                    await start(profile, budget.Token);
                    startup = timer.ElapsedMilliseconds;
                    var result = await probe(plan.Target, budget.Token);
                    var outcome = result.Success ? "Success" : result.Failure == ProbeFailure.TargetResponse ? "TargetResponse" : "Reachability";
                    attempts.Add(new(index, profile.Core.ToString(), repetition + 1, outcome, startup, result.Milliseconds));
                }
                catch (OperationCanceledException) when (budget.IsCancellationRequested)
                {
                    attempts.Add(new(index, profile.Core.ToString(), repetition + 1, "Canceled", startup, 0));
                    throw;
                }
                catch (OperationCanceledException)
                { attempts.Add(new(index, profile.Core.ToString(), repetition + 1, "StartupTimeout", timer.ElapsedMilliseconds, 0)); }
                catch (UserError)
                { attempts.Add(new(index, profile.Core.ToString(), repetition + 1, startup == 0 ? "CoreRejected" : "CoreStopped", timer.ElapsedMilliseconds, 0)); }
                finally { stop(); }
                if (plan.IntervalMilliseconds > 0) await Task.Delay(plan.IntervalMilliseconds, budget.Token);
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { canceled = true; }
        finally { stop(); }

        var summary = attempts.GroupBy(a => a.ProfileIndex).Select(group =>
        {
            var times = group.Where(a => a.Outcome == "Success").Select(a => a.RequestMilliseconds).Order().ToArray();
            long? median = times.Length == 0 ? null : times.Length % 2 == 1
                ? times[times.Length / 2] : times[times.Length / 2 - 1] + (times[times.Length / 2] - times[times.Length / 2 - 1]) / 2;
            return new LabSummary(group.Key, group.Count(), times.Length, (double)times.Length / group.Count(), median);
        }).ToArray();
        return new(canceled, attempts, summary);
    }
}
