using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oblachka;

// Новая версия на странице релизов GitHub.
// Revision — ревизия из имени установщика; Prerelease — пробная версия (тег vX.Y.Z-rc.N).
public sealed record AppRelease(Version Version, string Tag, Uri Installer, long Size, string Sha256, string Notes, Uri Page)
{
    public string FileName => Installer.Segments[^1];
    public string Revision { get; init; } = "";
    public bool Prerelease { get; init; }
    // «<установщик>.sig» из того же релиза (ReleaseSignature). Нет — релиз виден, но не ставится: «ждёт подписи».
    public Uri? Signature { get; init; }
}

// Обновление без переустановки: последний релиз репозитория (или пробный — по желанию), установщик из него,
// проверка SHA256 по digest, который GitHub считает сам при загрузке файла в релиз, а digest — по подписи
// офлайн-ключом владельца (ReleaseSignature): одному GitHub клиент не верит.
//
// В сеть уходит только запрос к api.github.com и скачивание установщика по нажатию:
// без идентификатора устройства и сведений о подписках. Ставит установщик, запущенный
// с запросом прав администратора: служба SYSTEM ничего скачанного не запускает.
public static class AppUpdate
{
    public const string Repository = "radiypoloniy/oblachkavpn";
    public static readonly Uri LatestApi = new("https://api.github.com/repos/" + Repository + "/releases/latest");
    // Для пробных: releases/latest их не отдаёт, список релизов — новые первыми.
    public static readonly Uri ListApi = new("https://api.github.com/repos/" + Repository + "/releases?per_page=10");
    private const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";
    public const long MaxInstallerBytes = 300L * 1024 * 1024;
    private static readonly Regex InstallerName = new(@"^Oblachka-setup-(\d+\.\d+\.\d+)-([0-9a-f]{7,12})\.exe$", RegexOptions.CultureInvariant);
    private static readonly Regex TagName = new(@"^v(\d+\.\d+\.\d+)(-rc\.\d+)?$", RegexOptions.CultureInvariant);

    public static Version Current { get; } = Version.Parse(ServiceBrand.Version);

    // Разбор ответа releases/latest. null — релиз не подходит: черновик, пробный (если их не
    // просили), без установщика, без контрольной суммы или с адресом не из этого репозитория.
    public static AppRelease? Parse(string json, bool prerelease = false) => JsonNode.Parse(json) is JsonObject root ? Parse(root, prerelease) : null;

    // Разбор списка релизов: первый подходящий, пробные тоже.
    public static AppRelease? ParseList(string json) => JsonNode.Parse(json) is JsonArray list
        ? list.OfType<JsonObject>().Select(r => Parse(r, true)).FirstOrDefault(r => r is not null)
        : null;

    private static AppRelease? Parse(JsonObject root, bool allowPrerelease)
    {
        var tag = root["tag_name"]?.GetValue<string>() ?? "";
        var tagMatch = TagName.Match(tag);
        var pre = root["prerelease"]?.GetValue<bool>() == true || tagMatch.Groups[2].Success;
        if (root["draft"]?.GetValue<bool>() == true || pre && !allowPrerelease || !tagMatch.Success) return null;
        var version = Version.Parse(tagMatch.Groups[1].Value);
        var assets = (root["assets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        foreach (var asset in assets)
        {
            var name = asset["name"]?.GetValue<string>() ?? "";
            var match = InstallerName.Match(name);
            if (!match.Success || Version.Parse(match.Groups[1].Value) != version) continue;
            var url = asset["browser_download_url"]?.GetValue<string>() ?? "";
            var digest = asset["digest"]?.GetValue<string>() ?? "";
            var size = asset["size"]?.GetValue<long>() ?? 0;
            if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal) || !url.EndsWith("/" + name, StringComparison.Ordinal) ||
                !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest[7..].All(char.IsAsciiHexDigit) ||
                size <= 0 || size > MaxInstallerBytes) return null;
            // Подписи ещё нет — релиз виден, но не ставится; подпись странная (чужой адрес, размер) — релиза нет.
            var sig = assets.FirstOrDefault(a => a["name"]?.GetValue<string>() == name + ReleaseSignature.Suffix);
            var sigUrl = sig?["browser_download_url"]?.GetValue<string>() ?? "";
            var sigSize = sig?["size"]?.GetValue<long>() ?? 0;
            if (sig is not null && (sigUrl != url + ReleaseSignature.Suffix || sigSize <= 0 || sigSize > ReleaseSignature.MaxBytes)) return null;
            var page = Uri.TryCreate(root["html_url"]?.GetValue<string>(), UriKind.Absolute, out var html) && html.Host == "github.com"
                ? html : new Uri("https://github.com/" + Repository + "/releases");
            var notes = root["body"]?.GetValue<string>() ?? "";
            return new AppRelease(version, tag, new Uri(url), size, digest[7..].ToUpperInvariant(), notes.Length > 4000 ? notes[..4000] : notes, page)
                { Revision = match.Groups[2].Value, Prerelease = pre, Signature = sig is null ? null : new Uri(sigUrl) };
        }
        return null;
    }

    public static bool IsNewer(AppRelease release) => IsNewer(release, Current, BuildIdentity.Revision);

    // Новее по номеру — или тот же номер из другой ревизии: проба и выпуск одной версии
    // носят одинаковый номер, и стоящая проба rc.1 должна видеть rc.2 и итоговый выпуск.
    // Ревизия своей сборки неизвестна — только по номеру.
    public static bool IsNewer(AppRelease release, Version current, string revision)
    {
        if (release.Version != current) return release.Version > current;
        var mine = revision.Length >= 8 && revision.Take(8).All(char.IsAsciiHexDigit) ? revision[..8] : null;
        return mine is not null && release.Revision.Length >= 7 &&
            !mine.StartsWith(release.Revision[..Math.Min(8, release.Revision.Length)], StringComparison.OrdinalIgnoreCase);
    }

    private static HttpClient Client(bool redirects) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10)
    }) { Timeout = redirects ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(20) };

    public static async Task<AppRelease?> CheckAsync(bool prerelease, CancellationToken token)
    {
        using var client = Client(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, prerelease ? ListApi : LatestApi);
        request.Headers.UserAgent.ParseAdd(ServiceBrand.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        // 404 — релизов ещё нет.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new UserError("GitHub не ответил (HTTP " + (int)response.StatusCode + "). Проверим позже.");
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new UserError("Ответ GitHub слишком большой.");
        var json = await response.Content.ReadAsStringAsync(token);
        return json.Length > 1024 * 1024 ? throw new UserError("Ответ GitHub слишком большой.") : prerelease ? ParseList(json) : Parse(json);
    }

    public static string Folder => Path.Combine(ClientDataPaths.Root, "updates");

    // Скачивает установщик и сверяет размер и SHA256. Переход со страницы релиза на хранилище
    // файлов GitHub разрешён только на HTTPS-хосты GitHub.
    public static async Task<string> DownloadAsync(AppRelease release, IProgress<double>? progress, CancellationToken token)
    {
        if (release.Signature is null) throw new UserError(Unsigned);
        Directory.CreateDirectory(Folder);
        foreach (var old in Directory.EnumerateFiles(Folder, "*.exe")) TryDelete(old);
        var target = Path.Combine(Folder, release.FileName);
        var partial = target + ".part";
        using var client = Client(true);
        // Подпись — до установщика: неподписанный файл не стоит даже скачивать.
        await VerifySignatureAsync(client, release, token);
        HttpResponseMessage? response = null;
        try
        {
            response = await GetAsync(client, release.Installer, token);
            if (!response.IsSuccessStatusCode) throw new UserError("Не удалось скачать обновление (HTTP " + (int)response.StatusCode + ").");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    received += read;
                    if (received > release.Size) throw new UserError("Файл обновления больше заявленного. Скачивание остановлено.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    progress?.Report((double)received / release.Size);
                }
            }
            if (received != release.Size || Convert.ToHexString(hash.GetHashAndReset()) != release.Sha256)
                throw new UserError("Файл обновления не прошёл проверку контрольной суммы. Попробуйте ещё раз.");
            File.Move(partial, target, true);
            return target;
        }
        finally { response?.Dispose(); TryDelete(partial); }
    }

    private static async Task VerifySignatureAsync(HttpClient client, AppRelease release, CancellationToken token)
    {
        if (release.Signature is null) throw new UserError(Unsigned);
        using var response = await GetAsync(client, release.Signature, token);
        if (!response.IsSuccessStatusCode) throw new UserError("Не удалось скачать подпись обновления (HTTP " + (int)response.StatusCode + ").");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[ReleaseSignature.MaxBytes + 1];
        int length = 0, read;
        while (length < buffer.Length && (read = await input.ReadAsync(buffer.AsMemory(length), token)) > 0) length += read;
        if (length > ReleaseSignature.MaxBytes || !ReleaseSignature.Verify(release, System.Text.Encoding.ASCII.GetString(buffer, 0, length)))
            throw new UserError(Unsigned);
    }

    private const string Unsigned = "Обновление не подписано ключом Облачки, ставить его нельзя. Сообщите нам об этом, пожалуйста.";

    // GET с переходами только на HTTPS-хосты GitHub: со страницы релиза файл отдаёт его хранилище.
    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(ServiceBrand.UserAgent);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is < 300 or >= 400) return response;
            var next = response.Headers.Location is { } location ? new Uri(uri, location) : null;
            response.Dispose();
            if (hop >= 4 || next is null || !TrustedHost(next)) throw new UserError("Установщик перенаправлен на незнакомый адрес. Скачайте его со страницы релизов.");
            uri = next;
        }
    }

    internal static bool TrustedHost(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 &&
        (uri.Host == "github.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    // Установщик ставит поверх, закрывает окно и после установки открывает его снова (/RELAUNCH=1).
    // Права администратора спрашивает сама Windows: так у установщика в манифесте.
    public static void Launch(string installer) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true });

    public static void CleanUp()
    {
        try { if (Directory.Exists(Folder)) foreach (var file in Directory.EnumerateFiles(Folder)) TryDelete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
