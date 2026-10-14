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
            // Помощник до открытия канала может восстанавливать незавершённую сессию,
            // поэтому на подключение даётся больше, чем на обычную команду.
            timeout.CancelAfter(ConnectTimeout); await pipe.ConnectAsync(timeout.Token);
            HelperPipeIdentity.Server(pipe, helper);
            return new(pipe);
        }
        catch { await pipe.DisposeAsync(); throw; }
    }

    // Канал службы один на всех: пока он занят другим окном, подключение ждёт.
    public static async Task<HelperPipeClient> ConnectServiceAsync(int servicePid, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(".", HelperPipeIdentity.ServicePipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ServiceConnectTimeout); await pipe.ConnectAsync(timeout.Token);
            HelperPipeIdentity.ServiceServer(pipe, servicePid);
            return new(pipe);
        }
        catch { await pipe.DisposeAsync(); throw; }
    }

    public static readonly TimeSpan ServiceConnectTimeout = TimeSpan.FromSeconds(10);

    // Короткие команды и длинные — разные сроки. Acquire и Release ждут появления или
    // исчезновения сетевого адаптера, и это десятки секунд: на стенде клиент сдавался
    // через 10 секунд, рвал канал и помечал сессию как требующую восстановления, хотя
    // помощник в это время успешно доводил остановку до конца.
    public static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(40);
    public static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(60);
    public static TimeSpan TimeoutFor(HelperOperation operation) =>
        operation == HelperOperation.Heartbeat ? QuickTimeout : SessionTimeout;

    public async Task<HelperResponse> SendAsync(HelperOperation operation, NetworkLeaseRequest? lease, CancellationToken token)
    {
        var request = new HelperRequest(1, Guid.NewGuid(), operation, lease); HelperProtocol.Validate(request);
        await gate.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeoutFor(operation));
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
