using System.Net;
using System.Net.Http;

namespace Oblachka;

// Выходной адрес: с какого IP и из какой страны сайты видят подключение. Спрашиваем Cloudflare
// (cdn-cgi/trace: строки ip=… и loc=…) — тот же поставщик, что DoH туннеля, нового стороннего
// сервиса нет. Запрос идёт тем же путём, что трафик: в «Браузеры и программы» — через SOCKS ядра
// окна, в «Весь компьютер» — обычным запросом программы через туннель.
public sealed record ExitInfo(IPAddress Address, ServerCountry Country)
{
    public string Text => Address + (Country.Code is null ? "" : " (" + Country.Name + ")");
}

public static class ExitAddress
{
    public static readonly Uri Trace = new("https://cloudflare.com/cdn-cgi/trace");

    public static async Task<ExitInfo?> FetchAsync(int? socksPort, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = socksPort is not null, Proxy = socksPort is { } port ? new WebProxy($"socks5://127.0.0.1:{port}", false) : null,
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(8), PooledConnectionLifetime = TimeSpan.Zero
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        try { return Parse(await http.GetStringAsync(Trace, token).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return null; }
    }

    internal static ExitInfo? Parse(string trace)
    {
        var fields = trace.Split('\n').Select(l => l.Trim().Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        if (!fields.TryGetValue("ip", out var ip) || !IPAddress.TryParse(ip, out var address)) return null;
        var country = fields.TryGetValue("loc", out var loc) && loc.Length == 2 && loc.All(char.IsAsciiLetter) && loc.ToUpperInvariant() != "XX"
            ? CountryCatalog.FromCode(loc) : new ServerCountry(null, "");
        return new(address, country);
    }
}
