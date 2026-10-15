using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace Oblachka;

// Подготовка набора zapret службой: манифест (встроенный или более свежий из нашего репозитория),
// архив Flowseal с GitHub, сверка архива и каждого нужного файла. Файлы ложатся в
// ProgramData\Oblachka\zapret\<версия> — туда пишут только SYSTEM и администраторы.
public static class ZapretInstaller
{
    public static string Root => Path.Combine(NetworkService.DataRoot, "zapret");
    public static string Folder(ZapretManifest manifest) => Path.Combine(Root, manifest.Tag);

    // Более свежий манифест из репозитория принимается, только если он целиком проходит проверку.
    public static async Task<ZapretManifest> ManifestAsync(CancellationToken token)
    {
        var embedded = ZapretManifest.Embedded();
        try
        {
            using var client = Client();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await client.GetAsync(ZapretManifest.RepositoryUrl, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 1024 * 1024) return embedded;
            var fetched = ZapretManifest.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            return fetched.Updated > embedded.Updated ? fetched : embedded;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UserError or IOException) { return embedded; }
    }

    // Готов ли набор на диске: все файлы манифеста на месте и совпадают по хешу.
    public static bool Ready(ZapretManifest manifest) => manifest.Files.All(f => Matches(Path.Combine(Folder(manifest), f.Key), f.Value));

    // Какие исполняемые файлы не совпали — обычно их удалил или подменил антивирус.
    public static string[] Damaged(ZapretManifest manifest) =>
        ZapretManifest.Executables.Where(e => !Matches(Path.Combine(Folder(manifest), e), manifest.Files[e])).ToArray();

    public static async Task InstallAsync(ZapretManifest manifest, CancellationToken token)
    {
        if (Ready(manifest)) { Generated(manifest); return; }
        Directory.CreateDirectory(Root);
        var archive = Path.Combine(Root, "download.zip");
        var staging = Folder(manifest) + ".new";
        try
        {
            await DownloadAsync(manifest, archive, token).ConfigureAwait(false);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            using (var zip = ZipFile.OpenRead(archive))
                foreach (var (path, hash) in manifest.Files)
                {
                    var entry = zip.GetEntry(manifest.Root + "/" + path) ?? throw new UserError("zapret: в архиве нет " + path + ".");
                    if (entry.Length > 16 * 1024 * 1024) throw new UserError("zapret: файл в архиве слишком большой.");
                    var target = Path.Combine(staging, path.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target);
                    if (!Matches(target, hash)) throw new UserError("zapret: файл " + path + " не совпал с манифестом.");
                }
            if (Directory.Exists(Folder(manifest))) Directory.Delete(Folder(manifest), true);
            Directory.Move(staging, Folder(manifest));
        }
        finally
        {
            Try(() => File.Delete(archive));
            Try(() => { if (Directory.Exists(staging)) Directory.Delete(staging, true); });
        }
        Generated(manifest);
        // Прежние версии набора больше не нужны (запущенной из них нет: перед установкой zapret остановлен).
        foreach (var old in Directory.EnumerateDirectories(Root).Where(d => !string.Equals(d, Folder(manifest), StringComparison.OrdinalIgnoreCase)))
            Try(() => Directory.Delete(old, true));
    }

    // Списки пользователя, на которые ссылаются стратегии: пустые, если их ещё нет.
    private static void Generated(ZapretManifest manifest)
    {
        foreach (var path in ZapretManifest.Generated)
        {
            var file = Path.Combine(Folder(manifest), path.Replace('/', '\\'));
            if (!File.Exists(file)) File.WriteAllText(file, "");
        }
    }

    private static async Task DownloadAsync(ZapretManifest manifest, string path, CancellationToken token)
    {
        using var client = Client();
        var uri = new Uri(manifest.Url);
        for (var hop = 0; ; hop++)
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var next = response.Headers.Location is { } location ? new Uri(uri, location) : null;
                if (hop >= 4 || next is null || !AppUpdate.TrustedHost(next)) throw new UserError("zapret: архив перенаправлен на незнакомый адрес.");
                uri = next; continue;
            }
            if (!response.IsSuccessStatusCode) throw new UserError("zapret: не удалось скачать набор (HTTP " + (int)response.StatusCode + ").");
            if (response.Content.Headers.ContentLength > 64 * 1024 * 1024) throw new UserError("zapret: архив слишком большой.");
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = File.Create(path)) await input.CopyToAsync(output, token).ConfigureAwait(false);
            break;
        }
        if (!Matches(path, manifest.Sha256)) throw new UserError("zapret: архив не совпал с манифестом.");
    }

    private static HttpClient Client() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(10) })
    { Timeout = TimeSpan.FromMinutes(3), DefaultRequestHeaders = { { "User-Agent", ServiceBrand.UserAgent } } };

    internal static bool Matches(string path, string hash)
    {
        try { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void Try(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
