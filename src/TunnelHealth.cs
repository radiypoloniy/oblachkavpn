namespace RoutePilot;

public sealed record HealthRound(bool Reachable, bool Verified, IReadOnlyList<ProbeResult> Results);

// A target HTTP error proves transport reachability; it must not trigger failover.
public sealed class TunnelHealth
{
    private int failedRounds;
    public int FailedRounds => failedRounds;
    public static readonly Uri[] Targets = [new("https://www.microsoft.com/"), new("https://www.cloudflare.com/")];

    public async Task<HealthRound> CheckAsync(Func<Uri, CancellationToken, Task<ProbeResult>> probe,
        IReadOnlyList<Uri> targets, CancellationToken token)
    {
        if (targets.Count is < 2 or > 4 || targets.Select(t => t.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2 ||
            targets.Any(t => t.Scheme != "https" || t.UserInfo.Length > 0)) throw new ArgumentException("Independent HTTPS targets required");
        var results = new List<ProbeResult>();
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            var result = await probe(target, token);
            results.Add(result);
            if (result.Success || result.Failure == ProbeFailure.TargetResponse) break;
        }
        var reachable = results.Any(r => r.Success || r.Failure == ProbeFailure.TargetResponse);
        failedRounds = reachable ? 0 : failedRounds + 1;
        return new(reachable, results.Any(r => r.Success), results);
    }
    public bool ShouldRecover => failedRounds >= 3;
    public void Reset() => failedRounds = 0;
}
