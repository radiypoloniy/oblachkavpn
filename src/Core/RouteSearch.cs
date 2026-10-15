namespace Oblachka;

public sealed record RouteAttempt(int ProfileIndex, string Outcome, long Milliseconds, bool Fragment = false);
// Fragment — рабочий вариант найден с разрезанным рукопожатием (TlsFragment).
public sealed record RouteSearchResult(int? ProfileIndex, IReadOnlyList<RouteAttempt> Attempts, bool Fragment = false);

// Bounded search shared by manual selection and opt-in health recovery. No direct fallback.
public sealed class RouteSearch
{
    public const int MaxAttempts = 6;
    private readonly Func<Profile, CancellationToken, Task> start;
    private readonly Func<Uri, CancellationToken, Task<ProbeResult>> probe;
    private readonly Action stop;
    private readonly Action<bool>? fragment;
    public RouteSearch(ITunnelController controller, Action<bool>? fragment = null) : this(controller.StartAsync, controller.ProbeAsync, controller.Stop, fragment) { }
    internal RouteSearch(Func<Profile, CancellationToken, Task> start, Func<Uri, CancellationToken, Task<ProbeResult>> probe, Action stop, Action<bool>? fragment = null)
    { this.start = start; this.probe = probe; this.stop = stop; this.fragment = fragment; }

    // Порядок перебора: выбранный, затем остальные по списку. Тем же порядком системный
    // режим заранее разрешает запасные узлы, чтобы подбор под блокировкой мог на них перейти.
    public static int[] Order(int count, int selected)
    {
        if (count <= 0) return Array.Empty<int>();
        var first = selected >= 0 && selected < count ? selected : 0;
        return new[] { first }.Concat(Enumerable.Range(0, count).Where(i => i != first)).Take(MaxAttempts).ToArray();
    }

    public Task<RouteSearchResult> FindAsync(IReadOnlyList<Profile> profiles, int selected, Uri target,
        CancellationToken token, Action<int, int>? progress = null) =>
        FindAsync(profiles, Order(profiles.Count, selected).Select(i => new LadderStep(i, false)).ToArray(), target, token, progress);

    // Шаги лестницы (RouteLadder): узел и включать ли разрезанное рукопожатие.
    public async Task<RouteSearchResult> FindAsync(IReadOnlyList<Profile> profiles, IReadOnlyList<LadderStep> steps, Uri target,
        CancellationToken token, Action<int, int>? progress = null)
    {
        if (profiles.Count == 0) throw new UserError("Сначала добавьте подключения.");
        if (target.Scheme != "https" && !(target.Scheme == "http" && target.IsLoopback) || target.UserInfo.Length > 0)
            throw new UserError("Для проверки нужен HTTPS-адрес без логина и пароля.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        var attempts = new List<RouteAttempt>();
        try
        {
            foreach (var (index, cut) in steps)
            {
                budget.Token.ThrowIfCancellationRequested(); stop();
                progress?.Invoke(attempts.Count + 1, steps.Count);
                fragment?.Invoke(cut);
                try
                {
                    await start(profiles[index], budget.Token);
                    var result = await probe(target, budget.Token);
                    budget.Token.ThrowIfCancellationRequested();
                    attempts.Add(new(index, result.Success ? "Verified" : result.Failure.ToString(), result.Milliseconds, cut));
                    if (result.Success) return new(index, attempts, cut);
                    if (result.Failure == ProbeFailure.TargetResponse)
                        throw new CheckSiteError();
                }
                catch (CheckSiteError) { throw; }
                catch (UserError) { attempts.Add(new(index, "StartOrProbeRejected", 0, cut)); }
            }
            stop(); return new(null, attempts);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { stop(); throw new UserError("Подбор остановлен по лимиту времени. Подключение выключено."); }
        catch { stop(); throw; }
    }
    private sealed class CheckSiteError() : UserError("Сайт проверки вернул HTTP-ошибку. Подбор остановлен; выберите другой адрес проверки.");
}
