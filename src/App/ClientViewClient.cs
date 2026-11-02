using System.Diagnostics;
using System.IO.Pipes;

namespace Oblachka;

public sealed class ClientViewClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly SemaphoreSlim gate = new(1);
    public Guid InstanceId { get; private set; }
    private ClientViewClient(NamedPipeClientStream pipe) { this.pipe = pipe; }
    public static async Task<ClientViewClient> ConnectAsync(string name, Process owner, CancellationToken token)
    {
        HelperPipeIdentity.ValidateName(name);
        _ = owner.SafeHandle;
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            HelperPipeIdentity.Server(pipe, owner);
            return new(pipe);
        }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async Task<ClientViewReply> SendAsync(ClientViewOperation operation, CancellationToken token, string? serverId = null, int offset = 0, bool allowReconnect = false)
    {
        if (!Enum.IsDefined(operation)) throw new UserError("Неизвестная команда интерфейса.");
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (InstanceId == Guid.Empty && operation != ClientViewOperation.ReadState)
            {
                var initial = await ExchangeAsync(ClientViewOperation.ReadState, token).ConfigureAwait(false);
                if (initial.Failure != ClientViewFailure.None) return initial;
            }
            return await ExchangeAsync(operation, token, serverId, offset, allowReconnect).ConfigureAwait(false);
        }
        catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
        finally { gate.Release(); }
    }
    private async Task<ClientViewReply> ExchangeAsync(ClientViewOperation operation, CancellationToken token, string? serverId = null, int offset = 0, bool allowReconnect = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(65));
        var request = new ClientViewRequest(ClientViewProtocol.Version, Guid.NewGuid(), InstanceId, operation, serverId, offset, allowReconnect);
        ClientViewProtocol.Validate(request);
        await HelperProtocol.WriteAsync(pipe, request, deadline.Token).ConfigureAwait(false);
        var reply = await HelperProtocol.ReadAsync<ClientViewReply>(pipe, deadline.Token).ConfigureAwait(false);
        ClientViewProtocol.Validate(reply, request.RequestId);
        if (reply.Failure == ClientViewFailure.None && (operation == ClientViewOperation.ListServers) != (reply.Servers is not null))
            throw new UserError("Ответ не соответствует команде интерфейса.");
        if (reply.State is { } state)
        {
            if (InstanceId != Guid.Empty && state.InstanceId != InstanceId) throw new UserError("Владелец подключения изменился.");
            InstanceId = state.InstanceId;
        }
        return reply;
    }
    public ValueTask DisposeAsync() => pipe.DisposeAsync();
}
