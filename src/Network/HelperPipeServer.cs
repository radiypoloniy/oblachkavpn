using System.Diagnostics;
using System.IO.Pipes;

namespace Oblachka;

// Сервер канала помощника с UAC: один владелец на весь запуск, его потеря снимает сессию.
// Служба (NetworkService) обслуживает тех же владельцев по очереди через ServeAsync, но
// при потере владельца сессию не снимает.
public sealed class HelperPipeServer(NetworkSession session, string pipeName, Process owner,
    TimeSpan? heartbeatTimeout = null, TimeSpan? sessionTimeout = null, Action<Exception>? report = null)
{
    // Обработка Acquire и Release ограничена отдельно и с запасом над ожиданием адаптера.
    public static readonly TimeSpan DefaultSessionTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan DefaultHeartbeatTimeout = TimeSpan.FromSeconds(15);

    public async Task RunAsync(CancellationToken token)
    {
        HelperPipeIdentity.ValidateName(pipeName);
        _ = owner.SafeHandle;
        // Recovery belongs to the trusted host startup, before accepting any caller.
        if (session.Recover() is { State: NetworkSessionState.RecoveryRequired })
            throw new UserError("Сначала требуется восстановить сетевую сессию.");
        try
        {
            using var pipe = HelperPipeIdentity.CreateServer(pipeName);
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(token))
            { wait.CancelAfter(TimeSpan.FromSeconds(15)); await pipe.WaitForConnectionAsync(wait.Token); }
            HelperPipeIdentity.Client(pipe, owner);
            await ServeAsync(session, pipe, owner, heartbeatTimeout, sessionTimeout, report, token);
        }
        finally
        {
            if (session.Recover() is { State: NetworkSessionState.RecoveryRequired })
                throw new UserError("Автоматическое восстановление сети не завершено.");
        }
    }

    // Обслуживание одного проверенного владельца до разрыва канала, тишины дольше срока
    // или его завершения. Сессию после этого не трогает: решает вызывающий.
    internal static async Task ServeAsync(NetworkSession session, Stream pipe, Process owner,
        TimeSpan? heartbeatTimeout, TimeSpan? sessionTimeout, Action<Exception>? report, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watcher = WatchOwnerAsync(owner, lifetime);
        try
        {
            Guid? activeLease = null;
            var recent = new HashSet<Guid>(); var order = new Queue<Guid>();
            while (!lifetime.IsCancellationRequested)
            {
                // Срок ожидания запроса и срок его обработки — разные вещи. Один общий
                // срок был ошибкой: Acquire и Release законно ждут появления или
                // исчезновения сетевого адаптера десятки секунд, срок истекал на записи
                // ответа, помощник завершался и рвал канал посреди успешной остановки.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                idle.CancelAfter(heartbeatTimeout ?? DefaultHeartbeatTimeout);
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
                    ZapretStatus? zapret = null;
                    if (request.Operation == HelperOperation.Acquire)
                    {
                        if (activeLease is not null) throw new UserError("Сессия уже запущена.");
                        // Сессия прежнего владельца (окно службы завершилось аварийно) снимается
                        // перед новой. У помощника с UAC её здесь не бывает: он снял её при запуске.
                        if (state is not (null or NetworkSessionState.Released) &&
                            session.Recover() is { State: NetworkSessionState.RecoveryRequired })
                            throw new UserError("Сначала требуется восстановить сетевую сессию.");
                        var record = session.Acquire(request.Lease!, deadline.Token);
                        // winws уже работал (режим «Браузеры»): его пакетам под защитой нужны разрешения.
                        if (record.State == NetworkSessionState.Applied) SyncReinjection();
                        state = record.State;
                        blocking = SystemNetworkBackend.Blocks(record);
                        activeLease = request.Lease!.LeaseId;
                    }
                    else if (request.Operation == HelperOperation.Release)
                    {
                        if (request.Lease!.LeaseId != activeLease) throw new UserError("Сессия принадлежит другому владельцу.");
                        state = session.Release(activeLease.Value).State;
                        if (state == NetworkSessionState.Released) WfpReinjection.Forget();
                        if (state == NetworkSessionState.Released) activeLease = null;
                    }
                    else if (request.Operation == HelperOperation.Repair)
                    {
                        // Ответ «исправлено» переносится полем Blocking: отдельного поля в ответе нет.
                        blocking = NetworkRepairBackend.Apply(request.Repair!);
                    }
                    else if (request.Operation == HelperOperation.Zapret)
                    {
                        zapret = await ZapretRunner.HandleAsync(request.Zapret!, deadline.Token);
                        if (state == NetworkSessionState.Applied) SyncReinjection();
                    }
                    else if (request.Operation == HelperOperation.Recover)
                    {
                        // Только чужая, оставленная сессия: свою владелец снимает через Release.
                        if (activeLease is not null) throw new UserError("Сессия этого владельца ещё активна.");
                        state = session.Recover()?.State ?? NetworkSessionState.Released;
                    }
                    response = new(1, request.RequestId, state == NetworkSessionState.RecoveryRequired ? HelperFailure.RecoveryRequired : HelperFailure.None, state, blocking, zapret);
                }
                catch (Exception ex) when (ex is UserError or IOException or UnauthorizedAccessException)
                {
                    // Отказ системы — это ответ, а не конец работы. Раньше сюда попадал
                    // только UserError, и отказ брандмауэра или адаптера проходил мимо
                    // ответа, завершая помощника посреди живого канала: клиент получал
                    // обрыв потока и не мог сказать пользователю ничего внятного.
                    var state = session.State;
                    var failure = state == NetworkSessionState.RecoveryRequired ? HelperFailure.RecoveryRequired
                        : ex is UserError ? HelperFailure.InvalidRequest : HelperFailure.SystemRefused;
                    // Причина записывается рядом с журналом: по каналу идёт только вид
                    // отказа, а сообщения в нём нет намеренно — в нём могли бы оказаться
                    // адреса узлов. Разбирать отказ без причины нечем.
                    report?.Invoke(ex);
                    response = new(1, request.RequestId, failure, state);
                }
                await HelperProtocol.WriteAsync(pipe, response, deadline.Token);
            }
        }
        finally { lifetime.Cancel(); await watcher; }
    }

    // Разрешения для пакетов zapret под защитой: есть winws — на его порты, нет — снять.
    // Повторно отправленные WinDivert пакеты Windows иначе режет как соединения от System.
    internal static Func<IWfpEngine> ReinjectionEngine { get; set; } = WfpEngine.Open;
    private static void SyncReinjection()
    {
        if (ZapretRunner.CapturedPorts is { Count: > 0 } ports) WfpReinjection.Allow(ReinjectionEngine, ports);
        else WfpReinjection.Revoke(ReinjectionEngine);
    }

    private static async Task WatchOwnerAsync(Process owner, CancellationTokenSource lifetime)
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
