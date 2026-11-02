using System.Text.Json.Serialization;

namespace Oblachka;

// Узкий канал представления. Здесь нет конфигураций, ключей, URI для запросов или имён методов.
public enum ClientViewOperation { ReadState, Probe, Stop, ListServers, SelectServer, Connect, Pause, CancelPause }
public enum ClientViewFailure { None, InvalidRequest, OwnerChanged, Busy, NotConnected, OperationFailed, RequiresReconnect, ConsentRequired }
public sealed record ClientViewRequest(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] Guid InstanceId,
    [property: JsonRequired] ClientViewOperation Operation,
    string? ServerId = null, int Offset = 0, bool AllowReconnect = false, int PauseMinutes = 0);
public sealed record ClientViewState(
    [property: JsonRequired] Guid InstanceId,
    [property: JsonRequired] ConnectionState Connection,
    [property: JsonRequired] SystemTunnelState? Tunnel,
    [property: JsonRequired] bool Busy,
    [property: JsonRequired] bool StopRequested,
    [property: JsonRequired] string? ServerName,
    [property: JsonRequired] int ProfileCount,
    [property: JsonRequired] int LocalPort,
    [property: JsonRequired] bool? Verified,
    [property: JsonRequired] string? SelectedServerId = null,
    [property: JsonRequired] string? RunningServerId = null,
    [property: JsonRequired] ClientConnectionResult ConnectionResult = ClientConnectionResult.None,
    [property: JsonRequired] DateTimeOffset? PausedUntil = null);
public sealed record ClientViewReply(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid RequestId,
    [property: JsonRequired] ClientViewFailure Failure,
    [property: JsonRequired] ClientViewState? State,
    [property: JsonRequired] ClientServerPage? Servers = null);

internal static class ClientViewProtocol
{
    internal const int Version = 3;
    internal static void Validate(ClientViewRequest request)
    {
        if (request.Version != Version || request.RequestId == Guid.Empty || !Enum.IsDefined(request.Operation) ||
            request.Operation != ClientViewOperation.ReadState && request.InstanceId == Guid.Empty)
            throw new UserError("Неизвестная команда интерфейса.");
        if (request.ServerId is not null && (!ClientServerCatalog.ValidId(request.ServerId) ||
            request.Operation is not (ClientViewOperation.SelectServer or ClientViewOperation.Connect)) ||
            request.Operation == ClientViewOperation.SelectServer && request.ServerId is null ||
            request.Offset is < 0 or > ProfileGroup.MaxTotalProfiles || request.Operation != ClientViewOperation.ListServers && request.Offset != 0 ||
            request.AllowReconnect && request.Operation is not (ClientViewOperation.SelectServer or ClientViewOperation.Connect))
            throw new UserError("Неверные параметры команды интерфейса.");
        if (request.Operation == ClientViewOperation.Pause ? request.PauseMinutes is not (5 or 15 or 60) : request.PauseMinutes != 0)
            throw new UserError("Неверный срок паузы.");
    }
    internal static void Validate(ClientViewReply reply, Guid requestId)
    {
        if (reply.Version != Version || reply.RequestId != requestId || !Enum.IsDefined(reply.Failure) ||
            (reply.Failure == ClientViewFailure.None) != (reply.State is not null))
            throw new UserError("Неверный ответ владельца подключения.");
        if (reply.State is { } state && (state.InstanceId == Guid.Empty || !Enum.IsDefined(state.Connection) ||
            state.Tunnel is { } tunnel && !Enum.IsDefined(tunnel) || state.ProfileCount < 0 ||
            state.LocalPort is < 0 or > 65535 || state.ServerName?.Length > 256 || !Enum.IsDefined(state.ConnectionResult) ||
            state.SelectedServerId is not null && !ClientServerCatalog.ValidId(state.SelectedServerId) ||
            state.RunningServerId is not null && !ClientServerCatalog.ValidId(state.RunningServerId)))
            throw new UserError("Неверное состояние подключения.");
        if (reply.Servers is { } page && (reply.Failure != ClientViewFailure.None || page.Items is null || page.Items.Length > ClientServerCatalog.PageSize ||
            page.Offset < 0 || page.Total < page.Offset + page.Items.Length || page.Items.Any(p => p is null || !ClientServerCatalog.ValidId(p.Id) || p.Name is null || p.Name.Length > 256)))
            throw new UserError("Неверная страница серверов.");
    }
}
