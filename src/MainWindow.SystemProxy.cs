using Microsoft.Win32;

namespace Oblachka;

// Optional per-user Windows proxy pointing at the running core's local port (SOCKS5 + HTTP on one port).
public partial class MainWindow
{
    private SystemProxy? systemProxy;
    private int systemProxyPort;

    private void InitSystemProxy(bool enabled)
    {
        Settings.SystemProxy.Click += (_, _) => { QueueStateSave(); UpdateModeText(); SyncSystemProxy(engine.State); };
        UpdateModeText();
        if (!enabled) { Settings.SystemProxy.IsEnabled = false; return; }
        systemProxy = SystemProxy.ForCurrentUser();
        // Undo a proxy left behind by a crash or power loss before anything else runs.
        TrySystemProxy(() => { if (systemProxy.Restore()) DetailText.Text = "Восстановлены прежние настройки прокси Windows после прошлого запуска."; });
        SystemEvents.SessionEnding += SessionEndingRestore;
    }

    private void SyncSystemProxy(ConnectionState state)
    {
        if (systemProxy is null) return;
        var wanted = Settings.SystemProxy.IsChecked == true && state is ConnectionState.Running or ConnectionState.Verified && engine.Port > 0;
        if (wanted && systemProxyPort != engine.Port)
            TrySystemProxy(() => { systemProxy.Apply(engine.Port); systemProxyPort = engine.Port; });
        // A crashed core (Failed) keeps the proxy: browsers stop instead of silently going direct until the user stops or recovery restarts it.
        else if (!wanted && (state == ConnectionState.Idle || Settings.SystemProxy.IsChecked != true) && (systemProxyPort != 0 || systemProxy.IsApplied))
            TrySystemProxy(() => { systemProxy.Restore(); systemProxyPort = 0; });
    }

    private void CloseSystemProxy()
    {
        SystemEvents.SessionEnding -= SessionEndingRestore;
        if (systemProxy is not null) TrySystemProxy(() => systemProxy.Restore());
        systemProxyPort = 0;
    }

    private void SessionEndingRestore(object? sender, SessionEndingEventArgs args)
    {
        try { systemProxy?.Restore(); } catch (Exception ex) when (ex is UserError or IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    private void TrySystemProxy(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is UserError or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { DetailText.Text = ex is UserError ? ex.Message : "Не удалось изменить настройки прокси Windows. Проверьте их вручную: Параметры → Сеть и Интернет → Прокси."; }
    }

    // Режим в подвале главного экрана и отметки в его меню.
    private void UpdateModeText()
    {
        var tunnel = SystemTunnelWanted;
        var proxy = !tunnel && Settings.SystemProxy.IsChecked == true;
        Home.ModeText.Text = tunnel ? "Весь компьютер" : proxy ? "Браузеры и программы" : "Только SOCKS";
        MarkRadio(ModeTunRadio, tunnel);
        MarkRadio(ModeProxyRadio, proxy);
        ModeTunNote.Text = NetworkHelperConnection.ServiceRunning
            ? "Системный VPN: игры и любые программы."
            : "Системный VPN: игры и любые программы. Windows спросит права администратора.";
    }
}
