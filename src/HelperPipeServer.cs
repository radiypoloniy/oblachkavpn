using System.Diagnostics;
using System.IO.Pipes;

namespace Oblachka;

public sealed class HelperPipeServer(NetworkSession session, string pipeName, Process owner,
    TimeSpan? heartbeatTimeout = null, TimeSpan? sessionTimeout = null)
{
    // Обработка Acquire и Release ограничена отдельно и с запасом над ожиданием адаптера.
    public static readonly TimeSpan DefaultSessionTimeout = TimeSpan.FromSeconds(45);

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
                // Срок ожидания запроса и срок его обработки — разные вещи. Один общий
                // срок был ошибкой: Acquire и Release законно ждут появления или
                // исчезновения сетевого адаптера десятки секунд, срок истекал на записи
                // ответа, помощник завершался и рвал канал посреди успешной остановки.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                idle.CancelAfter(heartbeatTimeout ?? TimeSpan.FromSeconds(15));
                var request = await HelperProtocol.ReadAsync<HelperRequest>(pipe, idle.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(sessionTimeout ?? DefaultSessionTimeout);
                HelperResponse response;
                try
                {
                    HelperProtocol.Validate(request);
                    if (!recent.Add(request.RequestId)) throw new UserError("Повторная команда helper.");
                    order.Enqueue(request.RequestId); if (order.Count > 64) recent.Remove(order.Dequeue());
                    NetworkSessionState? state = session.State;
                    bool? blocking = null;
                    if (request.Operation == HelperOperation.Acquire)
                    {
                        if (activeLease is not null) throw new UserError("Сессия уже запущена.");
                        var record = session.Acquire(request.Lease!, deadline.Token);
                        state = record.State;
                        blocking = SystemNetworkBackend.Blocks(record);
                        activeLease = request.Lease!.LeaseId;
                    }
                    else if (request.Operation == HelperOperation.Release)
                    {
                        if (request.Lease!.LeaseId != activeLease) throw new UserError("Сессия принадлежит другому владельцу.");
                        state = session.Release(activeLease.Value).State;
                        if (state == NetworkSessionState.Released) activeLease = null;
                    }
                    response = new(1, request.RequestId, state == NetworkSessionState.RecoveryRequired ? HelperFailure.RecoveryRequired : HelperFailure.None, state, blocking);
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
