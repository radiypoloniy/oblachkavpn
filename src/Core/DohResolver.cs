using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Oblachka;

// Адреса серверов VPN. Обычно — системный DNS; если он не ответил (провайдер режет или
// подменяет имена), — DoH по IP, мимо DNS провайдера. Если провайдер подписки попросил
// (server-address-resolve-enable), сначала его DoH. Запрос — RFC 8484 (application/dns-message):
// его понимает любой DoH-сервер.
public static class DohResolver
{
    public static readonly ServerResolve Cloudflare = new("https://cloudflare-dns.com/dns-query", "1.1.1.1");
    public static readonly ServerResolve Google = new("https://dns.google/dns-query", "8.8.8.8");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // DoH провайдера подписки сервиса; меняет окно, когда приходит подписка.
    public static ServerResolve? Preferred { get; set; }

    public static Task<IPAddress[]> ResolveAsync(string host, CancellationToken token) => ResolveAsync(host, false, token);

    // service — узел подписки сервиса. DoH сервиса спрашивают только о его узлах: имена серверов
    // сторонних подписок ему знать незачем (аудит 19.10, Р-5).
    public static async Task<IPAddress[]> ResolveAsync(string host, bool service, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out var literal)) return new[] { literal };
        var preferred = service ? Preferred : null;
        if (preferred is not null && await TryDohAsync(preferred, host, token).ConfigureAwait(false) is { Length: > 0 } first) return first;
        // Только частные адреса от DNS сети — похоже на заглушку провайдера: сначала DoH, а если
        // и он не ответил, то всё же они (сервер бывает и в локальной сети).
        var stub = Array.Empty<IPAddress>();
        try
        {
            var system = Usable(await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false));
            if (system.Any(a => !IsPrivate(a))) return system;
            stub = system;
        }
        catch (SocketException) { }
        foreach (var doh in new[] { Cloudflare, Google }.Where(d => d != preferred))
            if (await TryDohAsync(doh, host, token).ConfigureAwait(false) is { Length: > 0 } found) return found;
        if (stub.Length > 0) return stub;
        throw new UserError($"Не удалось узнать адрес сервера {host}: не ответили ни DNS сети, ни DoH.");
    }

    private static async Task<IPAddress[]?> TryDohAsync(ServerResolve doh, string host, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(Timeout);
        try
        {
            using var client = Client(doh);
            var v4 = QueryAsync(client, doh, host, 1, budget.Token);
            var v6 = QueryAsync(client, doh, host, 28, budget.Token);
            // AAAA дожидается и при упавшем A-запросе: клиент закрывается только после обеих задач.
            var aaaa = SafeAsync(v6);
            IPAddress[] a;
            try { a = await v4.ConfigureAwait(false); }
            finally { await aaaa.ConfigureAwait(false); }
            return Usable(a.Concat(await aaaa.ConfigureAwait(false)).ToArray());
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException or IOException or SocketException)
        { token.ThrowIfCancellationRequested(); return null; }
    }

    private static async Task<IPAddress[]> SafeAsync(Task<IPAddress[]> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or FormatException or IOException) { return Array.Empty<IPAddress>(); }
    }

    // Соединение — всегда на bootstrap-IP, имя DoH только в TLS и Host: системный DNS не нужен.
    private static HttpClient Client(ServerResolve doh)
    {
        var bootstrap = IPAddress.Parse(doh.Bootstrap);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
            ConnectCallback = async (context, ct) =>
            {
                var socket = new Socket(bootstrap.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try { await socket.ConnectAsync(new IPEndPoint(bootstrap, context.DnsEndPoint.Port), ct).ConfigureAwait(false); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { Timeout = Timeout };
    }

    private static async Task<IPAddress[]> QueryAsync(HttpClient client, ServerResolve doh, string host, ushort type, CancellationToken token)
    {
        using var content = new ByteArrayContent(Query(host, type));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        using var request = new HttpRequestMessage(HttpMethod.Post, doh.DohUrl) { Content = content };
        request.Headers.Accept.ParseAdd("application/dns-message");
        using var response = await client.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65535) throw new HttpRequestException("DoH: " + (int)response.StatusCode);
        return Answers(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false), type);
    }

    internal static byte[] Query(string host, ushort type)
    {
        string name;
        try { name = new IdnMapping().GetAscii(host.TrimEnd('.')); }
        catch (ArgumentException) { throw new FormatException("Имя сервера не подходит для DNS."); }
        var labels = name.Split('.');
        if (name.Length > 253 || labels.Any(l => l.Length is 0 or > 63)) throw new FormatException("Имя сервера слишком длинное.");
        var message = new List<byte> { 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in labels) { message.Add((byte)label.Length); message.AddRange(label.Select(c => (byte)c)); }
        message.AddRange(new byte[] { 0, (byte)(type >> 8), (byte)type, 0, 1 });
        return message.ToArray();
    }

    internal static IPAddress[] Answers(byte[] message, ushort type)
    {
        if (message.Length < 12 || (message[3] & 0x0F) != 0) throw new FormatException("DoH: ошибка ответа.");
        int questions = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4)), answers = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6));
        var offset = 12;
        for (var i = 0; i < questions; i++) offset = SkipName(message, offset) + 4;
        var result = new List<IPAddress>();
        for (var i = 0; i < answers; i++)
        {
            offset = SkipName(message, offset);
            if (offset + 10 > message.Length) throw new FormatException("DoH: обрезанный ответ.");
            var recordType = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8));
            offset += 10;
            if (offset + length > message.Length) throw new FormatException("DoH: обрезанный ответ.");
            if (recordType == type && length == (type == 1 ? 4 : 16)) result.Add(new IPAddress(message.AsSpan(offset, length)));
            offset += length;
        }
        return result.ToArray();
    }

    private static int SkipName(byte[] message, int offset)
    {
        while (offset < message.Length)
        {
            var length = message[offset];
            if ((length & 0xC0) == 0xC0) return offset + 2;
            if (length == 0) return offset + 1;
            offset += length + 1;
        }
        throw new FormatException("DoH: обрезанное имя.");
    }

    // Подменённые ответы: 0.0.0.0 и свой компьютер. Заглушки в локальной сети — в ResolveAsync.
    private static IPAddress[] Usable(IPAddress[] addresses) => addresses
        .Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)
        .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(a)).Distinct().ToArray();

    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 ||
            b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] is >= 64 and <= 127;
    }
}
