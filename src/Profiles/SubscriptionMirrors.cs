namespace Oblachka;

public sealed class SubscriptionAccessDenied() : UserError("Доступ к подписке отклонён (HTTP 401/403). Проверьте ключ и срок подписки в панели сервиса. Автообновление приостановлено; после решения проблемы нажмите «Обновить». Сохранённые подключения остаются в списке.");
public sealed record MirrorResult(ImportResult Import, int SourceIndex, int Attempts)
{
    // Адреса после переезда (new-url) и запасного адреса (fallback-url); null — прежние.
    public IReadOnlyList<string>? Sources { get; init; }
    public bool Moved { get; init; }
}

public static class SubscriptionMirrors
{
    public const int MaxSources = 5;
    public static IReadOnlyList<string> ParseSources(string input)
    {
        if (input.Length > 20 * 1024) throw new UserError("Список адресов слишком большой.");
        var sources = input.Split(new[] { '\r', '\n' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (sources.Length is < 1 or > MaxSources) throw new UserError("Укажите от одного до пяти полных HTTPS-адресов подписки, каждый с новой строки.");
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
                    return await MigrateAsync(validated, new(imported, index, attempts), fetch, token);
                }
                catch (SubscriptionAccessDenied) { throw; }
                catch (UserError) { }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new UserError("Истекло время загрузки подписки через резервные адреса. Текущий список сохранён."); }
        throw new UserError($"Не удалось загрузить подписку: проверено адресов {attempts}. Текущий список сохранён.");
    }

    // Провайдер сообщил новый адрес подписки (new-url / new-domain) или запасной (fallback-url).
    // Новый адрес принимается, только если с него загрузилась настоящая подписка; прежний
    // остаётся запасным — вдруг новый заблокируют раньше. Для подписки сервиса новый домен
    // запоминается как домен сервиса: иначе на него не ушёл бы идентификатор устройства.
    internal static async Task<MirrorResult> MigrateAsync(IReadOnlyList<string> sources, MirrorResult result,
        Func<string, CancellationToken, Task<ImportResult>> fetch, CancellationToken token)
    {
        if (result.Import.Metadata?.Provider is not { } provider) return result;
        var service = ServiceKey.SourceOf(sources) == ProfileSource.Service;
        var list = sources.ToList();
        if (provider.FallbackUrl is { } fallback && !list.Contains(fallback) && list.Count < MaxSources)
        {
            if (service) ServiceBrand.Learn(new Uri(fallback).Host);
            list.Add(fallback);
        }
        if (provider.NewUrl is { } moved && list[0] != moved)
        {
            var host = new Uri(moved).Host;
            var learned = service && ServiceBrand.Learn(host);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var imported = await fetch(moved, budget.Token);
                if (imported.Profiles.Count > 0)
                {
                    list.Remove(moved); list.Insert(0, moved);
                    if (list.Count > MaxSources) list.RemoveAt(list.Count - 1);
                    return new(imported, 0, result.Attempts + 1) { Sources = list, Moved = true };
                }
            }
            catch (UserError) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            if (learned) ServiceBrand.Forget(host);
        }
        return list.Count == sources.Count ? result : result with { Sources = list };
    }
}
