namespace RoutePilot;

public sealed record RouteAttempt(int ProfileIndex, string Outcome, long Milliseconds);
public sealed record RouteSearchResult(int? ProfileIndex, IReadOnlyList<RouteAttempt> Attempts);

// Explicit, bounded search only. No scheduled probes, hidden node changes or direct traffic fallback.
public sealed class RouteSearch
{
    public const int MaxAttempts = 6;
    private readonly Func<Profile, CancellationToken, Task> start;
    private readonly Func<Uri, CancellationToken, Task<ProbeResult>> probe;
    private readonly Action stop;
    public RouteSearch(ITunnelController controller) : this(controller.StartAsync, controller.ProbeAsync, controller.Stop) { }
    internal RouteSearch(Func<Profile, CancellationToken, Task> start, Func<Uri, CancellationToken, Task<ProbeResult>> probe, Action stop)
    { this.start = start; this.probe = probe; this.stop = stop; }

    public async Task<RouteSearchResult> FindAsync(IReadOnlyList<Profile> profiles, int selected, Uri target,
        CancellationToken token, Action<int, int>? progress = null)
    {
        if (profiles.Count == 0) throw new UserError("Сначала добавь подключения.");
        if (target.Scheme != "https" && !(target.Scheme == "http" && target.IsLoopback) || target.UserInfo.Length > 0)
            throw new UserError("Для проверки нужен HTTPS-адрес без логина и пароля.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        var first = selected >= 0 && selected < profiles.Count ? selected : 0;
        var order = new[] { first }.Concat(Enumerable.Range(0, profiles.Count).Where(i => i != first)).Take(MaxAttempts).ToArray();
        var attempts = new List<RouteAttempt>();
        try
        {
            foreach (var index in order)
            {
                budget.Token.ThrowIfCancellationRequested(); stop();
                progress?.Invoke(attempts.Count + 1, order.Length);
                try
                {
                    await start(profiles[index], budget.Token);
                    var result = await probe(target, budget.Token);
                    budget.Token.ThrowIfCancellationRequested();
                    attempts.Add(new(index, result.Success ? "Verified" : result.Failure.ToString(), result.Milliseconds));
                    if (result.Success) return new(index, attempts);
                    if (result.Failure == ProbeFailure.TargetResponse)
                        throw new CheckSiteError();
                }
                catch (CheckSiteError) { throw; }
                catch (UserError) { attempts.Add(new(index, "StartOrProbeRejected", 0)); }
            }
            stop(); return new(null, attempts);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { stop(); throw new UserError("Подбор остановлен по лимиту времени. Подключение выключено."); }
        catch { stop(); throw; }
    }
    private sealed class CheckSiteError() : UserError("Сайт проверки вернул HTTP-ошибку. Подбор остановлен; выбери другой адрес проверки.");
}
