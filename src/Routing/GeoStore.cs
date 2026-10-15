using System.Text.Json;

namespace Oblachka;

// Базы адресов geosite.dat и geoip.dat в папке клиента. Скачиваются по согласию пользователя
// (по адресам из маршрутизации сервиса или Loyalsoldier по умолчанию), проверяются разбором
// и заменяют прежние только целиком. Разобранные категории кэшируются до смены файла.
public sealed class GeoStore(string folder)
{
    public enum Kind { Geosite, Geoip }
    public sealed record FileInfo(Kind Kind, DateTimeOffset Updated, long Bytes, string Source);

    public static GeoStore Default { get; } = new(Path.Combine(ClientDataPaths.Root, "geo"));
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);

    private readonly object gate = new();
    private readonly Dictionary<string, object> cache = new();

    public string PathOf(Kind kind) => Path.Combine(folder, kind == Kind.Geosite ? "geosite.dat" : "geoip.dat");
    private string MetaOf(Kind kind) => PathOf(kind) + ".json";

    public FileInfo? Info(Kind kind)
    {
        try
        {
            if (!File.Exists(PathOf(kind)) || !File.Exists(MetaOf(kind))) return null;
            return JsonSerializer.Deserialize<FileInfo>(File.ReadAllText(MetaOf(kind)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public bool Has(Kind kind) => Info(kind) is not null;

    public async Task DownloadAsync(Kind kind, string url, CancellationToken token, IProgress<double>? progress = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new UserError("Адрес базы должен начинаться с https://.");
        Directory.CreateDirectory(folder);
        var staged = PathOf(kind) + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            // Системный прокси учитывается: при подключении в режиме «Браузеры» база идёт через VPN.
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(ServiceBrand.UserAgent);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new UserError("Сервер базы ответил ошибкой " + (int)response.StatusCode + ".");
            var total = response.Content.Headers.ContentLength;
            if (total > GeoDat.MaxFileBytes) throw new UserError("База адресов слишком большая.");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; long read = 0; int n;
                while ((n = await input.ReadAsync(buffer, token)) > 0)
                {
                    read += n;
                    if (read > GeoDat.MaxFileBytes) throw new UserError("База адресов слишком большая.");
                    await output.WriteAsync(buffer.AsMemory(0, n), token);
                    if (total > 0) progress?.Report((double)read / total.Value);
                }
            }
            var bytes = await File.ReadAllBytesAsync(staged, token);
            if (!GeoDat.LooksValid(bytes)) throw new UserError("Скачанный файл — не база адресов.");
            File.Move(staged, PathOf(kind), true);
            await File.WriteAllTextAsync(MetaOf(kind), JsonSerializer.Serialize(new FileInfo(kind, DateTimeOffset.UtcNow, bytes.LongLength, uri.Host)), token);
            lock (gate) cache.Clear();
        }
        catch (HttpRequestException) { throw new UserError("Не удалось скачать базу адресов. Проверьте интернет и попробуйте ещё раз."); }
        catch (TaskCanceledException) when (!token.IsCancellationRequested) { throw new UserError("Скачивание базы адресов не уложилось в 5 минут."); }
        finally { try { File.Delete(staged); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    public Dictionary<string, List<GeoDat.SiteEntry>> Sites(IReadOnlyCollection<string> tags) =>
        Cached("site:" + string.Join(',', tags.Order()), () => Has(Kind.Geosite) ? GeoDat.ReadSites(File.ReadAllBytes(PathOf(Kind.Geosite)), tags) : new());

    public Dictionary<string, List<string>> Ips(IReadOnlyCollection<string> tags) =>
        Cached("ip:" + string.Join(',', tags.Order()), () => Has(Kind.Geoip) ? GeoDat.ReadIps(File.ReadAllBytes(PathOf(Kind.Geoip)), tags) : new());

    private T Cached<T>(string key, Func<T> load) where T : class
    {
        lock (gate)
        {
            if (cache.TryGetValue(key, out var value)) return (T)value;
            T result;
            try { result = load(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { result = (T)Activator.CreateInstance(typeof(T))!; }
            cache[key] = result;
            return result;
        }
    }
}
