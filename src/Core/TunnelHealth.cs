namespace Oblachka;

public sealed record HealthRound(bool Reachable, bool Verified, IReadOnlyList<ProbeResult> Results);

// A target HTTP error proves transport reachability; it must not trigger failover.
public sealed class TunnelHealth
{
    private int failedRounds;
    public int FailedRounds => failedRounds;
    public static readonly Uri[] Defaults = [new("https://www.microsoft.com/"), new("https://www.cloudflare.com/")];
    // Свой адрес проверки («Для продвинутых») — первым; второй встроенный остаётся: проверке нужны
    // два независимых сайта, иначе отказ одного сайта выглядел бы как обрыв туннеля.
    public static Uri? Custom { get; set; }
    public static Uri[] Targets => Custom is not { } custom ? Defaults
        : [custom, Defaults.First(d => !string.Equals(d.Host, custom.Host, StringComparison.OrdinalIgnoreCase))];

    // Сайты спрашиваются одновременно: по очереди молчащий сервер стоил 10 + 10 с на одну проверку.
    // Первый ответ отменяет остальные. budget — срок всей проверки: сразу после подключения ждать
    // молчащий сервер по 10 с незачем (MainWindow.Health); не уложился — это неудача, а не отмена.
    public async Task<HealthRound> CheckAsync(Func<Uri, CancellationToken, Task<ProbeResult>> probe,
        IReadOnlyList<Uri> targets, CancellationToken token, TimeSpan? budget = null)
    {
        if (targets.Count is < 2 or > 4 || targets.Select(t => t.Host).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2 ||
            targets.Any(t => t.Scheme != "https" || t.UserInfo.Length > 0)) throw new ArgumentException("Independent HTTPS targets required");
        token.ThrowIfCancellationRequested();
        using var round = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (budget is { } limit) round.CancelAfter(limit);
        async Task<ProbeResult> One(Uri target)
        {
            var started = Environment.TickCount64;
            try
            {
                var result = await probe(target, round.Token);
                if (result.Success || result.Failure == ProbeFailure.TargetResponse) round.Cancel();
                return result;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { return new(false, Environment.TickCount64 - started, "Сайт проверки не ответил вовремя.", ProbeFailure.Reachability); }
        }
        var results = await Task.WhenAll(targets.Select(One));
        token.ThrowIfCancellationRequested();
        var reachable = results.Any(r => r.Success || r.Failure == ProbeFailure.TargetResponse);
        failedRounds = reachable ? 0 : failedRounds + 1;
        return new(reachable, results.Any(r => r.Success), results);
    }
    public bool ShouldRecover => failedRounds >= 3;
    public void Reset() => failedRounds = 0;
}
