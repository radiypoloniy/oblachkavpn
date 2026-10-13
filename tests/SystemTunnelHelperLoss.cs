using System.Diagnostics;

namespace Oblachka;

// Сценарий «помощник погиб» на живом системном режиме. Фильтры WFP не динамические,
// поэтому защита обязана пережить помощника: сеть закрыта, мимо туннеля ничего. Снимает
// её новый запуск помощника — при старте он восстанавливает сеть по журналу. Это тот же
// путь, что у кнопки «Восстановить сеть» в окне.
internal static class SystemTunnelHelperLoss
{
    internal static void Run(TunnelEndpointPlan plan, Engine engine, Action<string> add, Func<string> probe)
    {
        HelperProcessLifetime? helper = null;
        try
        {
            using var token = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            helper = HelperProcessLifetime.ConnectAsync(NetworkHelperLauncher.Launch, token.Token).GetAwaiter().GetResult();
            var session = new SystemTunnelSession(engine, helper.Client);
            session.StartAsync(plan, token.Token).GetAwaiter().GetResult();
            var helpers = Process.GetProcessesByName("Oblachka").Where(p => p.Id != Environment.ProcessId).ToArray();
            add("смерть помощника: процессов помощника " + helpers.Length + ", выход до: " + probe());
            foreach (var p in helpers) { try { p.Kill(true); } catch (Exception) { } }
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (session.State is SystemTunnelState.Running or SystemTunnelState.Stopping && DateTime.UtcNow < deadline) Thread.Sleep(200);
            engine.Stop();
            add("смерть помощника: состояние " + session.State + ", " + WfpStateProbe.Describe().Split(';')[0]);
            add("смерть помощника: запрос без туннеля: " + probe());
            add("смерть помощника: журнал " + NetworkSessionJournal.PeekState(NetworkHelperHost.JournalPath));
            var again = HelperProcessLifetime.ConnectAsync(NetworkHelperLauncher.Launch, token.Token).GetAwaiter().GetResult();
            again.DisposeAsync().AsTask().GetAwaiter().GetResult();
            add("смерть помощника: после нового запуска помощника журнал " + NetworkSessionJournal.PeekState(NetworkHelperHost.JournalPath) +
                ", " + WfpStateProbe.Describe().Split(';')[0] + ", выход: " + probe());
            Thread.Sleep(5000);
            add("смерть помощника: выход через 5 с: " + probe());
        }
        catch (Exception ex) { add("смерть помощника: сценарий прерван (" + ex.GetType().Name + (ex is UserError ? ": " + ex.Message : "") + ")"); }
        finally { try { helper?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception) { } }
    }
}
