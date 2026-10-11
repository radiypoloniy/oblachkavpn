using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoutePilot;

public enum HelperOperation { Acquire, Release, Heartbeat }
public enum HelperFailure { None, InvalidRequest, Busy, RecoveryRequired }
public sealed record HelperRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] HelperOperation Operation,
    [property: JsonRequired] NetworkLeaseRequest? Lease);
public sealed record HelperResponse(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] HelperFailure Failure,
    [property: JsonRequired] NetworkSessionState? State);

public static class HelperProtocol
{
    public const int MaxBytes = 8192;
    private static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaxBytes) throw new UserError("Неверный размер сообщения helper.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        return Parse<T>(bytes);
    }

    public static T Parse<T>(byte[] bytes)
    {
        if (bytes.Length is < 1 or > MaxBytes) throw new UserError("Неверный размер сообщения helper.");
        try
        {
            using var document = JsonDocument.Parse(bytes);
            Unique(document.RootElement);
            return document.RootElement.Deserialize<T>(Json) ?? throw new UserError("Пустое сообщение helper.");
        }
        catch (JsonException) { throw new UserError("Неподдерживаемое сообщение helper."); }
    }

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length is < 1 or > MaxBytes) throw new UserError("Сообщение helper слишком большое.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }

    public static void Validate(HelperRequest request)
    {
        if (request.Version != 1 || request.RequestId == Guid.Empty || !Enum.IsDefined(request.Operation))
            throw new UserError("Неизвестная команда helper.");
        if (request.Operation == HelperOperation.Heartbeat)
        {
            if (request.Lease is not null) throw new UserError("Heartbeat не принимает сетевые параметры.");
            return;
        }
        if (request.Lease is null) throw new UserError("Отсутствует команда сетевой сессии.");
        var lease = NetworkHelperContracts.Validate(request.Lease);
        if (request.Operation == HelperOperation.Acquire && lease.Action != NetworkLeaseAction.Acquire ||
            request.Operation == HelperOperation.Release && lease.Action != NetworkLeaseAction.Release)
            throw new UserError("Команда helper не соответствует сетевой сессии.");
    }

    private static void Unique(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in node.EnumerateObject())
            { if (!names.Add(field.Name)) throw new UserError("Поле сообщения повторяется."); Unique(field.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Unique(item);
    }
}
