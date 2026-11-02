using System.Text.Json.Serialization;

namespace Oblachka;

// Узкий канал представления. Здесь нет конфигураций, ключей, URI для запросов или имён методов.
public enum ClientViewOperation { ReadState, Probe, Stop }
public enum ClientViewFailure { None, InvalidRequest, OwnerChanged, Busy, NotConnected, OperationFailed }
public sealed record ClientViewRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] Guid InstanceId,
    [property: JsonRequired] ClientViewOperation Operation);
public sealed record ClientViewState(
    [property: JsonRequired] Guid InstanceId,
    [property: JsonRequired] ConnectionState Connection,
    [property: JsonRequired] SystemTunnelState? Tunnel,
    [property: JsonRequired] bool Busy,
    [property: JsonRequired] bool StopRequested,
    [property: JsonRequired] string? ServerName,
    [property: JsonRequired] int ProfileCount,
    [property: JsonRequired] int LocalPort,
    [property: JsonRequired] bool? Verified);
public sealed record ClientViewReply(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] ClientViewFailure Failure,
    [property: JsonRequired] ClientViewState? State);

internal static class ClientViewProtocol
{
    internal static void Validate(ClientViewRequest request)
    {
        if (request.Version != 1 || request.RequestId == Guid.Empty || !Enum.IsDefined(request.Operation) ||
            request.Operation != ClientViewOperation.ReadState && request.InstanceId == Guid.Empty)
            throw new UserError("Неизвестная команда интерфейса.");
    }
    internal static void Validate(ClientViewReply reply, Guid requestId)
    {
        if (reply.Version != 1 || reply.RequestId != requestId || !Enum.IsDefined(reply.Failure) ||
            (reply.Failure == ClientViewFailure.None) != (reply.State is not null))
            throw new UserError("Неверный ответ владельца подключения.");
        if (reply.State is { } state && (state.InstanceId == Guid.Empty || !Enum.IsDefined(state.Connection) ||
            state.Tunnel is { } tunnel && !Enum.IsDefined(tunnel) || state.ProfileCount < 0 ||
            state.LocalPort is < 0 or > 65535 || state.ServerName?.Length > 256))
            throw new UserError("Неверное состояние подключения.");
    }
}
