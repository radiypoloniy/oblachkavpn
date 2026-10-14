using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oblachka;

// Recover снимает сессию, оставленную прежним владельцем: у службы она переживает окно.
public enum HelperOperation { Acquire, Release, Heartbeat, Recover }
// SystemRefused — отказ самой системы, а не ошибка в запросе. Без него такой отказ
// вылетал мимо ответа и завершал помощника: клиент видел обрыв канала вместо причины.
public enum HelperFailure { None, InvalidRequest, Busy, RecoveryRequired, SystemRefused }
public sealed record HelperRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] HelperOperation Operation,
    [property: JsonRequired] NetworkLeaseRequest? Lease);
// Blocking говорит, встала ли блокировка трафика мимо туннеля. Поле нужно затем,
// что помощник решает это сам: при выключенном брандмауэре блокировки не будет, и
// окно обязано сообщить пользователю то, что есть, а не то, на что мы рассчитывали.
public sealed record HelperResponse(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] HelperFailure Failure,
    [property: JsonRequired] NetworkSessionState? State,
    [property: JsonRequired] bool? Blocking = null);

public static class HelperProtocol
{
    // С запасом на 100 правил сайтов и 100 правил программ.
    public const int MaxBytes = 32768;
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
        if (request.Operation is HelperOperation.Heartbeat or HelperOperation.Recover)
        {
            if (request.Lease is not null) throw new UserError(request.Operation + " не принимает сетевые параметры.");
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
