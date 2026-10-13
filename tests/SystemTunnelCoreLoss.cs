namespace Oblachka;

// Сценарий «ядро умерло» на живом системном режиме: процесс ядра завершается снаружи,
// как это сделал бы сбой. Сессия обязана перейти в удержание, не снимая блокировку,
// а перезапуск — вернуть связь без нового туннеля.
internal static class SystemTunnelCoreLoss
{
    internal static void Run(SystemTunnelSession session, Engine engine, Action<string> add, Func<string> probe)
    {
        var pid = engine.ProcessId;
        if (pid is null) { add("смерть ядра: ядра нет, сценарий пропущен"); return; }
        var interfaces = string.Join(',', TunnelInterfaces.Ours());
        try { System.Diagnostics.Process.GetProcessById(pid.Value).Kill(); }
        catch (Exception ex) { add("смерть ядра: завершить не удалось (" + ex.GetType().Name + ")"); return; }
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (session.State != SystemTunnelState.Holding && DateTime.UtcNow < deadline) Thread.Sleep(100);
        add("смерть ядра: состояние " + session.State + ", блокировка " + (session.BlocksBypass ? "держится" : "СНЯТА") +
            ", брандмауэр: " + KillSwitchProbe.Describe());
        add("смерть ядра: запрос при мёртвом ядре: " + probe());
        try
        {
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            session.SwitchAsync(null, token.Token).GetAwaiter().GetResult();
            add("смерть ядра: перезапуск — " + session.State + ", порт " + engine.Port +
                ", интерфейс " + (string.Join(',', TunnelInterfaces.Ours()) == interfaces ? "прежний" : "ДРУГОЙ"));
            Thread.Sleep(3000);
            add("смерть ядра: выходной IP после перезапуска: " + probe());
        }
        catch (Exception ex) { add("смерть ядра: перезапуск не удался (" + ex.GetType().Name + ": " + (ex is UserError ? ex.Message : "") + ")"); }
    }
}
