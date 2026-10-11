namespace RoutePilot;

public sealed class SubscriptionAccessDenied() : UserError("Подписка отклонила доступ (HTTP 401/403). Проверь срок действия и ссылку. Обход через резервный адрес не выполняется.");
public sealed record MirrorResult(ImportResult Import, int SourceIndex, int Attempts);

public static class SubscriptionMirrors
{
    public const int MaxSources = 5;
    public static IReadOnlyList<string> ParseSources(string input)
    {
        if (input.Length > 20 * 1024) throw new UserError("Список адресов слишком большой.");
        var sources = input.Split(new[] { '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (sources.Length is < 1 or > MaxSources) throw new UserError("Укажи от одного до пяти полных HTTPS-адресов подписки, каждый с новой строки.");
        foreach (var source in sources)
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0)
                throw new UserError("Каждая строка должна содержать полный HTTPS-адрес без логина, пароля и #fragment.");
        return sources;
    }
    public static Task<MirrorResult> FetchAsync(IReadOnlyList<string> sources, int preferred, CancellationToken token) =>
        FetchAsync(sources, preferred, (url, ct) => Subscriptions.FetchAsync(url, ct), token);

    internal static async Task<MirrorResult> FetchAsync(IReadOnlyList<string> sources, int preferred,
        Func<string, CancellationToken, Task<ImportResult>> fetch, CancellationToken token)
    {
        var validated = ParseSources(string.Join("\n", sources));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        var first = preferred >= 0 && preferred < validated.Count ? preferred : 0;
        var order = new[] { first }.Concat(Enumerable.Range(0, validated.Count).Where(i => i != first));
        var attempts = 0;
        try
        {
            foreach (var index in order)
            {
                budget.Token.ThrowIfCancellationRequested(); attempts++;
                try
                {
                    var imported = await fetch(validated[index], budget.Token);
                    budget.Token.ThrowIfCancellationRequested();
                    if (imported.Profiles.Count == 0) throw new UserError("Подписка пуста.");
                    return new(imported, index, attempts);
                }
                catch (SubscriptionAccessDenied) { throw; }
                catch (UserError) { }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new UserError("Истекло время загрузки подписки через резервные адреса. Текущий список сохранён."); }
        throw new UserError($"Не удалось загрузить подписку: проверено адресов {attempts}. Текущий список сохранён.");
    }
}
