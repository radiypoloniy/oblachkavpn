using System.Diagnostics;
using System.IO.Pipes;

namespace Oblachka;

public sealed class HelperPipeServer(NetworkSession session, string pipeName, Process owner, TimeSpan? heartbeatTimeout = null)
{
    public async Task RunAsync(CancellationToken token)
    {
        HelperPipeIdentity.ValidateName(pipeName);
        _ = owner.SafeHandle;
        // Recovery belongs to the trusted host startup, before accepting any caller.
        if (session.Recover() is { State: NetworkSessionState.RecoveryRequired })
            throw new UserError("Сначала требуется восстановить сетевую сессию.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watcher = WatchOwnerAsync(lifetime);
        try
        {
            using var pipe = HelperPipeIdentity.CreateServer(pipeName);
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            { wait.CancelAfter(TimeSpan.FromSeconds(15)); await pipe.WaitForConnectionAsync(wait.Token); }
            HelperPipeIdentity.Client(pipe, owner);
            Guid? activeLease = null;
            var recent = new HashSet<Guid>(); var order = new Queue<Guid>();
            while (!lifetime.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(heartbeatTimeout ?? TimeSpan.FromSeconds(15));
                var request = await HelperProtocol.ReadAsync<HelperRequest>(pipe, deadline.Token);
                HelperResponse response;
                try
                {
                    HelperProtocol.Validate(request);
                    if (!recent.Add(request.RequestId)) throw new UserError("Повторная команда helper.");
                    order.Enqueue(request.RequestId); if (order.Count > 64) recent.Remove(order.Dequeue());
                    NetworkSessionState? state = session.State;
                    if (request.Operation == HelperOperation.Acquire)
                    {
                        if (activeLease is not null) throw new UserError("Сессия уже запущена.");
                        state = session.Acquire(request.Lease!, deadline.Token).State;
                        activeLease = request.Lease!.LeaseId;
                    }
                    else if (request.Operation == HelperOperation.Release)
                    {
                        if (request.Lease!.LeaseId != activeLease) throw new UserError("Сессия принадлежит другому владельцу.");
                        state = session.Release(activeLease.Value).State;
                        if (state == NetworkSessionState.Released) activeLease = null;
                    }
                    response = new(1, request.RequestId, state == NetworkSessionState.RecoveryRequired ? HelperFailure.RecoveryRequired : HelperFailure.None, state);
                }
                catch (UserError)
                {
                    var state = session.State;
                    response = new(1, request.RequestId, state == NetworkSessionState.RecoveryRequired ? HelperFailure.RecoveryRequired : HelperFailure.InvalidRequest, state);
                }
                await HelperProtocol.WriteAsync(pipe, response, deadline.Token);
            }
        }
        finally
        {
            lifetime.Cancel(); await watcher;
            if (session.Recover() is { State: NetworkSessionState.RecoveryRequired })
                throw new UserError("Автоматическое восстановление сети не завершено.");
        }
    }

    private async Task WatchOwnerAsync(CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (owner.HasExited) { lifetime.Cancel(); return; }
                await Task.Delay(250, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
}
