using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoutePilot;

public enum NetworkLeaseAction { Acquire, Release }
public sealed record NetworkLeaseRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid LeaseId,
    [property: JsonRequired] NetworkLeaseAction Action,
    [property: JsonRequired] int LoopbackPort,
    [property: JsonRequired] string[] PeerAddresses);
public sealed record ValidatedNetworkLease(Guid LeaseId, NetworkLeaseAction Action, int LoopbackPort, IReadOnlyList<IPAddress> Peers);

// A future privileged helper accepts bounded network intent, never profiles, tokens or shell commands.
// This contract performs no IPC and changes no network settings.
public static class NetworkHelperContracts
{
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<NetworkLeaseAction>(allowIntegerValues: false) }
    };

    public static ValidatedNetworkLease Parse(string input)
    {
        if (Encoding.UTF8.GetByteCount(input) > 8192) throw new UserError("Запрос сетевого помощника слишком большой.");
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
            if (request.LoopbackPort != 0 || request.PeerAddresses.Length != 0) throw new UserError("Завершение сессии не принимает сетевые параметры.");
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
        return new(request.LeaseId, request.Action, request.LoopbackPort, peers.AsReadOnly());
    }
}
