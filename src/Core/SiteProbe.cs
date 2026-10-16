using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Oblachka;

// Что увидели на одном пути до сайта («Почему не открывается сайт?»).
//   Ok          — сайт ответил;
//   NoName      — имя не находится;          FakeAddress — DNS отдал адрес-заглушку;
//   Address     — до адреса не доходит соединение (и с чужим именем в рукопожатии тоже);
//   Handshake   — с именем сайта рукопожатие рвётся, с нейтральным проходит: DPI по имени;
//   Certificate — ответил не тот сайт (подмена);  BlockPage — страница блокировки провайдера;
//   Stall       — ответ начался и замер (замедление);  Forbidden — HTTP 403;  ServerError — HTTP 5xx;
//   Skipped     — этот путь сейчас не проверить (Note — почему).
public enum SiteStage { Ok, NoName, FakeAddress, Address, Handshake, Certificate, BlockPage, Stall, Forbidden, ServerError, Skipped }

public sealed record SitePath(SiteStage Stage, long Milliseconds, int Status = 0, string? Note = null)
{
    public bool Opens => Stage == SiteStage.Ok;
    public static SitePath Skip(string why) => new(SiteStage.Skipped, 0, 0, why);
}

// Путь через SOCKS: вход «напрямую» ядра туннеля (с паролем) или отдельное ядро с сервером VPN.
public sealed record SocksRoute(int Port, string? User = null, string? Password = null);

public static class SiteProbe
{
    // Имя, с которым проверяется рукопожатие, когда с именем сайта оно рвётся.
    private const string NeutralName = "example.com";
    private const int Enough = 16 * 1024, Limit = 64 * 1024;
    private static readonly string[] BlockMarkers =
    [
        "rkn.gov.ru", "eais.rkn", "blocklist.rkn", "zapret-info", "warning.rt.ru", "blocked.mts", "block.beeline",
        "доступ ограничен", "доступ к информационному ресурсу ограничен", "заблокирован по решению", "ограничен на основании"
    ];

    // Адрес или домен от человека → домен в ASCII (IDN) или null. Порт, путь и схема отбрасываются.
    public static string? NormalizeHost(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns) return null;
        try { return RoutingPlan.Create(uri.IdnHost, SiteAction.Tunnel).Domain; }
        catch (UserError) { return null; }
    }

    // Адреса, которые не могут принадлежать сайту в интернете: так DNS провайдера «прячет» сайт.
    internal static bool Bogus(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] is 0 or 10 or 127 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
    }

    public static async Task<SitePath> CheckAsync(string host, SocksRoute? via, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        IPAddress[] addresses = [];
        if (via is null)
        {
            try { addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false); }
            catch (SocketException) { return new(SiteStage.NoName, watch.ElapsedMilliseconds); }
            if (addresses.Length == 0) return new(SiteStage.NoName, watch.ElapsedMilliseconds);
            addresses = addresses.Where(a => !Bogus(a)).OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6).Take(2).ToArray();
            if (addresses.Length == 0) return new(SiteStage.FakeAddress, watch.ElapsedMilliseconds);
        }
        var first = await OpenAsync(host, via, addresses, token).ConfigureAwait(false);
        if (first is null)
            return via is null ? new(SiteStage.Address, watch.ElapsedMilliseconds)
                : await NameMissingAsync(host, token).ConfigureAwait(false) ? new(SiteStage.NoName, watch.ElapsedMilliseconds) : SitePath.Skip("ядро не приняло соединение");
        await using (first)
        {
            var (shake, ssl, certificateOk) = await HandshakeAsync(first, host, token).ConfigureAwait(false);
            if (shake != Shake.Done)
            {
                // С именем сайта не вышло: тот же адрес с нейтральным именем отличает DPI от недоступного адреса.
                await using var again = await OpenAsync(host, via, addresses, token).ConfigureAwait(false);
                var neutral = again is null ? Shake.Dropped : (await HandshakeAsync(again, NeutralName, token).ConfigureAwait(false)).Shake;
                if (neutral != Shake.Dropped) return new(SiteStage.Handshake, watch.ElapsedMilliseconds);
                return new(via is not null && await NameMissingAsync(host, token).ConfigureAwait(false) ? SiteStage.NoName : SiteStage.Address, watch.ElapsedMilliseconds);
            }
            await using (ssl!)
            {
                if (!certificateOk) return new(SiteStage.Certificate, watch.ElapsedMilliseconds);
                return await RequestAsync(ssl!, host, watch, token).ConfigureAwait(false);
            }
        }
    }

    private enum Shake { Done, Answered, Dropped }

    // Через SOCKS ядро на несуществующее имя просто не соединяет — похоже на закрытый адрес.
    // Различаем по DNS этого компьютера: «такого имени нет» отвечают одинаково на любом пути.
    private static async Task<bool> NameMissingAsync(string host, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        // DNS через туннель на несуществующее имя может думать больше 10 с (DoH с повторами).
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false); return false; }
        catch (SocketException ex) { return ex.SocketErrorCode == SocketError.HostNotFound; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    private static async Task<Stream?> OpenAsync(string host, SocksRoute? via, IPAddress[] addresses, CancellationToken token)
    {
        if (via is null)
            foreach (var address in addresses)
                if (await ConnectAsync(new IPEndPoint(address, 443), token).ConfigureAwait(false) is { } direct) return direct;
        if (via is null) return null;
        if (await ConnectAsync(new IPEndPoint(IPAddress.Loopback, via.Port), token).ConfigureAwait(false) is not { } proxy) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { if (await Socks5ConnectAsync(proxy, host, via, timeout.Token).ConfigureAwait(false)) return proxy; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (IOException) { }
        await proxy.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    private static async Task<Stream?> ConnectAsync(IPEndPoint endpoint, CancellationToken token)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try { await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { socket.Dispose(); return null; }
        catch (SocketException) { socket.Dispose(); return null; }
    }

    // SOCKS5 CONNECT по имени: адрес разрешает ядро — DNS того пути, который проверяется.
    private static async Task<bool> Socks5ConnectAsync(Stream stream, string host, SocksRoute via, CancellationToken token)
    {
        var auth = via.User is not null && via.Password is not null;
        await stream.WriteAsync(auth ? new byte[] { 5, 1, 2 } : new byte[] { 5, 1, 0 }, token).ConfigureAwait(false);
        var choice = await ReadExactAsync(stream, 2, token).ConfigureAwait(false);
        if (choice[0] != 5 || choice[1] != (auth ? 2 : 0)) return false;
        if (auth)
        {
            var user = Encoding.ASCII.GetBytes(via.User!); var pass = Encoding.ASCII.GetBytes(via.Password!);
            await stream.WriteAsync((byte[])[1, (byte)user.Length, .. user, (byte)pass.Length, .. pass], token).ConfigureAwait(false);
            if ((await ReadExactAsync(stream, 2, token).ConfigureAwait(false))[1] != 0) return false;
        }
        var name = Encoding.ASCII.GetBytes(host);
        await stream.WriteAsync((byte[])[5, 1, 0, 3, (byte)name.Length, .. name, 1, 187], token).ConfigureAwait(false);
        var head = await ReadExactAsync(stream, 4, token).ConfigureAwait(false);
        if (head[1] != 0) return false;
        var rest = head[3] switch { 1 => 4, 4 => 16, 3 => (await ReadExactAsync(stream, 1, token).ConfigureAwait(false))[0], _ => -1 };
        if (rest < 0) return false;
        await ReadExactAsync(stream, rest + 2, token).ConfigureAwait(false);
        return true;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
        return buffer;
    }

    // Done — рукопожатие завершено; Answered — сервер ответил отказом (значит, связь есть);
    // Dropped — оборвано или молчание: так ведёт себя фильтр или недоступный адрес.
    private static async Task<(Shake Shake, SslStream? Ssl, bool CertificateOk)> HandshakeAsync(Stream stream, string serverName, CancellationToken token)
    {
        var certificateOk = false;
        var ssl = new SslStream(stream, leaveInnerStreamOpen: true, (_, _, _, errors) => { certificateOk = errors == SslPolicyErrors.None; return true; });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            { TargetHost = serverName, ApplicationProtocols = [SslApplicationProtocol.Http11] }, timeout.Token).ConfigureAwait(false);
            return (Shake.Done, ssl, certificateOk);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { await ssl.DisposeAsync().ConfigureAwait(false); return (Shake.Dropped, null, false); }
        catch (System.Security.Authentication.AuthenticationException) { await ssl.DisposeAsync().ConfigureAwait(false); return (Shake.Answered, null, false); }
        catch (IOException) { await ssl.DisposeAsync().ConfigureAwait(false); return (Shake.Dropped, null, false); }
    }

    // GET / по HTTP/1.1 без сжатия: читаются заголовки и до 64 КБ тела.
    private static async Task<SitePath> RequestAsync(Stream ssl, string host, Stopwatch watch, CancellationToken token)
    {
        var request = $"GET / HTTP/1.1\r\nHost: {host}\r\nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36\r\n" +
            "Accept: text/html,*/*\r\nAccept-Language: ru,en;q=0.8\r\nConnection: close\r\n\r\n";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var data = new MemoryStream();
        var complete = false;
        try
        {
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token).ConfigureAwait(false);
            var buffer = new byte[16384];
            int read;
            while (data.Length < Limit && (read = await ssl.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0) data.Write(buffer, 0, read);
            complete = true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (IOException) { }
        return Classify(Encoding.UTF8.GetString(data.GetBuffer(), 0, (int)data.Length), complete || data.Length >= Limit, watch.ElapsedMilliseconds);
    }

    // Разбор ответа отдельно от сети — его проверяют тесты. complete — ответ дочитан (или набрали предел).
    internal static SitePath Classify(string response, bool complete, long milliseconds)
    {
        var end = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var status = response.StartsWith("HTTP/", StringComparison.Ordinal) && response.Split(' ', 3) is { Length: >= 2 } parts && int.TryParse(parts[1], out var code) ? code : 0;
        if (end < 0 || status == 0) return new(SiteStage.Stall, milliseconds);
        var text = response.ToLowerInvariant();
        if (status == 451 || BlockMarkers.Any(text.Contains)) return new(SiteStage.BlockPage, milliseconds, status);
        // Ответ замер на первых килобайтах — так фильтры режут сайты, пропуская начало.
        if (!complete && response.Length < Enough && !Short(response, end)) return new(SiteStage.Stall, milliseconds, status);
        return status switch
        {
            403 => new(SiteStage.Forbidden, milliseconds, status),
            >= 500 => new(SiteStage.ServerError, milliseconds, status),
            _ => new(SiteStage.Ok, milliseconds, status)
        };
    }

    // Тело уже пришло целиком по Content-Length: ждать больше нечего, это не замирание.
    private static bool Short(string response, int end)
    {
        foreach (var line in response[..end].Split("\r\n"))
            if (line.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase) && int.TryParse(line[15..].Trim(), out var length))
                return Encoding.UTF8.GetByteCount(response) - end - 4 >= length;
        return false;
    }
}
