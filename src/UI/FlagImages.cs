using System.Collections.Concurrent;
using System.Windows.Media.Imaging;

namespace Oblachka;

public static class FlagImages
{
    // Off: 252 flags are bundled; a direct CDN request would reveal the client and the user's real IP.
    public static bool AllowNetwork { get; set; }
    private static readonly SemaphoreSlim Slots = new(4);
    private static readonly ConcurrentDictionary<string, Lazy<Task<BitmapImage?>>> Images = new();
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    { Timeout = TimeSpan.FromSeconds(8) };

    public static BitmapImage? Local(string? code)
    {
        if (code is null) return null;
        try { return new BitmapImage(new Uri($"pack://application:,,,/Oblachka;component/Assets/Flags/{code.ToLowerInvariant()}.png")); }
        catch (IOException) { return null; }
    }

    public static Task<BitmapImage?> LoadAsync(string code) => Images.GetOrAdd(code,
        key => new Lazy<Task<BitmapImage?>>(() => DownloadAsync(key))).Value;

    private static async Task<BitmapImage?> DownloadAsync(string code)
    {
        if (!AllowNetwork || CountryCatalog.FromCode(code).Code is null) return null;
        await Slots.WaitAsync().ConfigureAwait(false);
        try
        {
            using var response = await Client.GetAsync($"https://flagcdn.com/w80/{code.ToLowerInvariant()}.png", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 65536) return null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[4096];
            int count;
            while ((count = await input.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > 65536) return null;
                buffer.Write(bytes, 0, count);
            }
            var data = buffer.ToArray();
            if (data.Length < 24 || !data.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return null;
            var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
            var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
            if (width is < 1 or > 160 || height is < 1 or > 160) return null;
            buffer.Position = 0;
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = buffer; image.EndInit(); image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or NotSupportedException or ArgumentException or FormatException)
        { return null; }
        finally { Slots.Release(); }
    }
}
