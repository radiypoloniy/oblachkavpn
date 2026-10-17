using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oblachka;

public enum NetworkLeaseAction { Acquire, Release }
public sealed record NetworkLeaseRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid LeaseId,
    [property: JsonRequired] NetworkLeaseAction Action,
    [property: JsonRequired] int LoopbackPort,
    [property: JsonRequired] string[] PeerAddresses,
    SiteRule[]? Sites = null,
    ProgramRule[]? Programs = null,
    // Ядро окна само отправляет часть трафика напрямую (списки маршрутизации). Отдаёт оно его
    // ядру туннеля через отдельный локальный вход SOCKS с паролем этой сессии, а ядро туннеля
    // выпускает всё, что пришло туда, мимо туннеля. Распознавание процесса для этого не годится:
    // на стенде правило по пути ядра не срабатывало, и трафик шёл по кругу.
    CoreDirectEntry? CoreDirect = null,
    // «Через VPN только выбранное»: всё, что не попало в правила «через VPN», идёт напрямую.
    bool DirectByDefault = false);

public sealed record CoreDirectEntry(
    [property: JsonRequired] int Port,
    [property: JsonRequired] string User,
    [property: JsonRequired] string Password)
{
    public static CoreDirectEntry Create(int port) => new(port, Secret(), Secret());
    private static string Secret() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
    public void Validate()
    {
        static bool Token(string? value) => value is { Length: >= 16 and <= 64 } && value.All(char.IsAsciiLetterOrDigit);
        if (Port is < 1024 or > 65535 || !Token(User) || !Token(Password))
            throw new UserError("Неверные параметры прямого выхода ядра.");
    }
}
// Правила сайтов и программ — ограниченные данные: домены и имена .exe без путей,
// проверенные теми же правилами, что и в окне. Конфигурацию ядра помощник строит сам.
public sealed record ValidatedNetworkLease(Guid LeaseId, NetworkLeaseAction Action, int LoopbackPort, IReadOnlyList<IPAddress> Peers,
    IReadOnlyList<SiteRule>? Sites = null, IReadOnlyList<ProgramRule>? Programs = null, CoreDirectEntry? CoreDirect = null, bool DirectByDefault = false)
{
    public IReadOnlyList<SiteRule> SiteRules => Sites ?? Array.Empty<SiteRule>();
    public IReadOnlyList<ProgramRule> ProgramRules => Programs ?? Array.Empty<ProgramRule>();
    // Есть ли трафик, который пользователь сам отправил мимо туннеля.
    public bool HasDirect => DirectByDefault || CoreDirect is not null || SiteRules.Any(r => r.Action == SiteAction.Direct) || ProgramRules.Any(r => r.Action == SiteAction.Direct);
}

// A future privileged helper accepts bounded network intent, never profiles, tokens or shell commands.
// This contract performs no IPC and changes no network settings.
public static class NetworkHelperContracts
{
    public const int MaxBytes = HelperProtocol.MaxBytes;
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static ValidatedNetworkLease Parse(string input)
    {
        if (Encoding.UTF8.GetByteCount(input) > MaxBytes) throw new UserError("Запрос сетевого помощника слишком большой.");
        try
        {
            using var document = JsonDocument.Parse(input);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new UserError("Неверная форма запроса помощника.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in document.RootElement.EnumerateObject())
                if (!names.Add(field.Name)) throw new UserError("Поле запроса помощника повторяется.");
            var request = JsonSerializer.Deserialize<NetworkLeaseRequest>(input, Json) ?? throw new UserError("Пустой запрос помощника.");
            return Validate(request);
        }
        catch (JsonException) { throw new UserError("Неподдерживаемый запрос сетевого помощника."); }
    }

    public static ValidatedNetworkLease Validate(NetworkLeaseRequest request)
    {
        if (request.Version != 1 || request.LeaseId == Guid.Empty || !Enum.IsDefined(request.Action) || request.PeerAddresses is null)
            throw new UserError("Неверная версия или идентификатор сетевой сессии.");
        if (request.Action == NetworkLeaseAction.Release)
        {
            if (request.LoopbackPort != 0 || request.PeerAddresses.Length != 0 || request.Sites is { Length: > 0 } || request.Programs is { Length: > 0 } || request.CoreDirect is not null || request.DirectByDefault)
                throw new UserError("Завершение сессии не принимает сетевые параметры.");
            return new(request.LeaseId, request.Action, 0, Array.Empty<IPAddress>());
        }
        if (request.LoopbackPort is < 1 or > 65535 || request.PeerAddresses.Length is < 1 or > 16)
            throw new UserError("Неверный порт или список адресов туннельного сервера.");
        var peers = new List<IPAddress>();
        foreach (var text in request.PeerAddresses)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Contains('%') || !IPAddress.TryParse(text, out var peer))
                throw new UserError("Помощник принимает только точные IP-адреса, без имён и диапазонов.");
            if (peer.IsIPv4MappedToIPv6) peer = peer.MapToIPv4();
            var bytes = peer.GetAddressBytes();
            if (bytes.Length == 4 && !text.Contains(':') && !text.Equals(peer.ToString(), StringComparison.Ordinal))
                throw new UserError("IPv4-адрес должен содержать четыре десятичных числа.");
            if (IPAddress.IsLoopback(peer) || peer.Equals(IPAddress.Any) || peer.Equals(IPAddress.IPv6Any) ||
                peer.Equals(IPAddress.Broadcast) || peer.IsIPv6Multicast || peer.IsIPv6LinkLocal ||
                bytes.Length == 4 && (bytes[0] == 0 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254))
                throw new UserError("Этот адрес нельзя использовать как внешний адрес туннельного сервера.");
            if (peers.Contains(peer)) throw new UserError("Адрес сервера повторяется.");
            peers.Add(peer);
        }
        var sites = request.Sites ?? Array.Empty<SiteRule>();
        var programs = request.Programs ?? Array.Empty<ProgramRule>();
        RoutingPlan.Validate(sites);
        ProgramRoutingPlan.Validate(programs);
        request.CoreDirect?.Validate();
        if (request.CoreDirect?.Port == request.LoopbackPort) throw new UserError("Прямой выход ядра совпадает с портом ядра.");
        return new(request.LeaseId, request.Action, request.LoopbackPort, peers.AsReadOnly(), sites, programs, request.CoreDirect, request.DirectByDefault);
    }
}
