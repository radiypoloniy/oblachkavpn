using System.Diagnostics;
using System.IO.Pipes;

namespace Oblachka;

public sealed class HelperPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly SemaphoreSlim gate = new(1);
    private HelperPipeClient(NamedPipeClientStream pipe) { this.pipe = pipe; }

    public static async Task<HelperPipeClient> ConnectAsync(string name, Process helper, CancellationToken token)
    {
        HelperPipeIdentity.ValidateName(name);
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10)); await pipe.ConnectAsync(timeout.Token);
            HelperPipeIdentity.Server(pipe, helper);
            return new(pipe);
        }
        catch { await pipe.DisposeAsync(); throw; }
    }

    public async Task<HelperResponse> SendAsync(HelperOperation operation, NetworkLeaseRequest? lease, CancellationToken token)
    {
        var request = new HelperRequest(1, Guid.NewGuid(), operation, lease); HelperProtocol.Validate(request);
        await gate.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await HelperProtocol.WriteAsync(pipe, request, timeout.Token);
            var response = await HelperProtocol.ReadAsync<HelperResponse>(pipe, timeout.Token);
            if (response.Version != 1 || response.RequestId != request.RequestId || !Enum.IsDefined(response.Failure) ||
                response.State is { } state && !Enum.IsDefined(state)) throw new UserError("Неверный ответ helper.");
            return response;
        }
        catch { await pipe.DisposeAsync(); throw; } // A timed out response cannot match the next request.
        finally { gate.Release(); }
    }

    public ValueTask DisposeAsync() => pipe.DisposeAsync();
}
