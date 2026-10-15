using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oblachka;

// Новая версия на странице релизов GitHub.
public sealed record AppRelease(Version Version, string Tag, Uri Installer, long Size, string Sha256, string Notes, Uri Page)
{
    public string FileName => Installer.Segments[^1];
}

// Обновление без переустановки: последний релиз репозитория, установщик из него,
// проверка SHA256 по digest, который GitHub считает сам при загрузке файла в релиз.
//
// В сеть уходит только запрос к api.github.com и скачивание установщика по нажатию:
// без идентификатора устройства и сведений о подписках. Ставит установщик, запущенный
// с запросом прав администратора: служба SYSTEM ничего скачанного не запускает.
public static class AppUpdate
{
    public const string Repository = "radiypoloniy/oblachkavpn";
    public static readonly Uri LatestApi = new("https://api.github.com/repos/" + Repository + "/releases/latest");
    private const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";
    public const long MaxInstallerBytes = 300L * 1024 * 1024;
    private static readonly Regex InstallerName = new(@"^Oblachka-setup-(\d+\.\d+\.\d+)-[0-9a-f]{7,12}\.exe$", RegexOptions.CultureInvariant);

    public static Version Current { get; } = Version.Parse(ServiceBrand.Version);

    // Разбор ответа releases/latest. null — релиз не подходит: черновик, без установщика,
    // без контрольной суммы или с адресом не из этого репозитория.
    public static AppRelease? Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root || root["draft"]?.GetValue<bool>() == true || root["prerelease"]?.GetValue<bool>() == true) return null;
        var tag = root["tag_name"]?.GetValue<string>() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version.Build < 0) return null;
        foreach (var asset in (root["assets"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
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
            var page = Uri.TryCreate(root["html_url"]?.GetValue<string>(), UriKind.Absolute, out var html) && html.Host == "github.com"
                ? html : new Uri("https://github.com/" + Repository + "/releases");
            var notes = root["body"]?.GetValue<string>() ?? "";
            return new AppRelease(version, tag, new Uri(url), size, digest[7..].ToUpperInvariant(), notes.Length > 4000 ? notes[..4000] : notes, page);
        }
        return null;
    }

    public static bool IsNewer(AppRelease release) => release.Version > Current;

    private static HttpClient Client(bool redirects) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10)
    }) { Timeout = redirects ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(20) };

    public static async Task<AppRelease?> CheckAsync(CancellationToken token)
    {
        using var client = Client(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.UserAgent.ParseAdd(ServiceBrand.UserAgent);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        // 404 — релизов ещё нет.
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new UserError("GitHub не ответил (HTTP " + (int)response.StatusCode + "). Проверим позже.");
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new UserError("Ответ GitHub слишком большой.");
        var json = await response.Content.ReadAsStringAsync(token);
        return json.Length > 1024 * 1024 ? throw new UserError("Ответ GitHub слишком большой.") : Parse(json);
    }

    public static string Folder => Path.Combine(ClientDataPaths.Root, "updates");

    // Скачивает установщик и сверяет размер и SHA256. Переход со страницы релиза на хранилище
    // файлов GitHub разрешён только на HTTPS-хосты GitHub.
    public static async Task<string> DownloadAsync(AppRelease release, IProgress<double>? progress, CancellationToken token)
    {
        Directory.CreateDirectory(Folder);
        foreach (var old in Directory.EnumerateFiles(Folder, "*.exe")) TryDelete(old);
        var target = Path.Combine(Folder, release.FileName);
        var partial = target + ".part";
        using var client = Client(true);
        var uri = release.Installer;
        HttpResponseMessage? response = null;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd(ServiceBrand.UserAgent);
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if ((int)response.StatusCode is < 300 or >= 400) break;
                var next = response.Headers.Location is { } location ? new Uri(uri, location) : null;
                response.Dispose(); response = null;
                if (hop >= 4 || next is null || !TrustedHost(next)) throw new UserError("Установщик перенаправлен на незнакомый адрес. Скачайте его со страницы релизов.");
                uri = next;
            }
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
