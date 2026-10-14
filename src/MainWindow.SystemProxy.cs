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

    private void UpdateModeText()
    {
        if (SystemTunnelWanted)
        {
            Connection.ModeTitle.Text = "Системный VPN";
            Connection.ModeSubtitle.Text = "Трафик компьютера через TUN";
            Connection.ModeNote.Text = NetworkHelperConnection.ServiceRunning
                ? "Весь трафик компьютера идёт через туннель, мимо него ничего не уходит. Режим меняется в настройках."
                : "Весь трафик компьютера идёт через туннель. Служба не установлена: при подключении Windows спросит права администратора.";
            return;
        }
        var system = Settings.SystemProxy.IsChecked == true;
        Connection.ModeTitle.Text = system ? "Системный прокси" : "Только через туннель";
        Connection.ModeSubtitle.Text = system ? "Браузеры и программы с настройками Windows" : "Программы с настроенным SOCKS5";
        Connection.ModeNote.Text = system
            ? "Пока подключение запущено, прокси Windows указывает на Облачку. После остановки прежние настройки возвращаются. Игры, UDP и программы без поддержки прокси идут напрямую."
            : "Сейчас туннель используют программы с настроенным SOCKS5. Остальные подключения компьютера не меняются. Системный прокси включается в настройках.";
    }
}
