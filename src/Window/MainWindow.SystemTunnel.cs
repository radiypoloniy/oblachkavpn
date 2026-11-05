using System.Windows;

namespace Oblachka;

// Системный режим: весь трафик компьютера через туннель.
//
// Привилегированную часть выполняет сетевая служба, если она установлена, иначе отдельный
// процесс, запускаемый по запросу с явным повышением прав. Окно прав администратора не
// требует и ядром в этом режиме не управляет: порядок запуска и остановки держит
// SystemTunnelSession.
public partial class MainWindow
{
    private SystemTunnelSession? systemTunnel => runtime.Tunnel;
    private IHelperLifetime? helperLifetime => runtime.Helper;
    private bool systemStopping;

    private bool SystemTunnelWanted => preferences.SystemTunnel;
    private bool SystemTunnelActive => systemTunnel is not null &&
        systemTunnel.State is not (SystemTunnelState.Idle or SystemTunnelState.Failed);

    private void InitSystemTunnel(bool enabled)
    {
        runtime.TunnelChanged += OnSystemTunnelChanged;
        runtime.ProtectionRepaired += OnProtectionRepaired;
        Settings.SystemTunnel.Click += SystemTunnelToggled;
        Settings.RestoreNetwork.Click += async (_, _) => await RestoreNetworkAsync();
        if (!enabled) { Settings.SystemTunnel.IsEnabled = false; return; }
        // Установленная служба — значит, системный VPN готов к работе без запроса прав: он и
        // включён по умолчанию. Сохранённый выбор пользователя применяется позже, при загрузке.
        var service = NetworkHelperConnection.ServiceRunning;
        preferences.SystemTunnel = service;
        if (service) preferences.SystemProxy = false;
        Settings.SystemTunnelHint.Text = "Весь трафик компьютера идёт через туннель, включая игры и программы без настроек прокси. " +
            (service ? "Работает через службу Облачков, права администратора не нужны."
                     : "Служба Облачков не установлена: права администратора запрашиваются при каждом включении.");
        UpdateModeText();
        UpdateSystemTunnelNote(null);
    }

    // Сессия, не снятая прошлым запуском, держит сеть закрытой: у помощника — потому что он
    // погиб, а фильтры остались; у службы — потому что окно завершилось аварийно, и служба
    // намеренно держит защиту. Окно предлагает восстановить сеть, а не молчит.
    private static bool NetworkLeftChanged => SessionLeft(NetworkHelperHost.JournalPath) || SessionLeft(NetworkService.JournalPath);
    private static bool SessionLeft(string journal) =>
        NetworkSessionJournal.PeekState(journal) is { } state && state != NetworkSessionState.Released;

    private async Task<bool> RestoreNetworkAsync()
    {
        if (operation is not null || runtime.Connections.IsStopping) return false;
        if (systemTunnel is { PinnedServices.Count: 0 }) return false;
        if (!AppDialog.Confirm(this, "Восстановить обычную сеть?",
            "Строгая защита будет снята. Программы смогут подключаться напрямую, если это разрешено настройкой «Без VPN — без интернета».",
            "Восстановить сеть", danger: true)) return false;
        Settings.RestoreNetwork.IsEnabled = false;
        var service = NetworkHelperConnection.ServiceRunning;
        DetailText.Text = service ? "Восстанавливаем сеть." : "Восстанавливаем сеть. Подтвердите запрос прав администратора.";
        var recovered = false;
        try
        {
            if (systemTunnel is not null) await runtime.Connections.StopAsync(preservePause: false, stopProtection: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var lifetime = await NetworkHelperConnection.ConnectAsync(timeout.Token);
            if (await lifetime.Client.RecoverAsync(timeout.Token) == NetworkSessionState.Released)
            {
                await lifetime.Client.SetStrictGuardAsync(arm: false, timeout.Token);
                recovered = true;
            }
        }
        catch (Exception ex) when (ex is UserError or IOException or TimeoutException or OperationCanceledException or ObjectDisposedException) { }
        finally { Settings.RestoreNetwork.IsEnabled = true; }
        DetailText.Text = recovered && !NetworkLeftChanged ? "Сеть восстановлена." : "Сеть восстановить не удалось. Проверьте, запущена ли служба Облачки.";
        UpdateSystemTunnelNote(null);
        return recovered;
    }

    private static async Task RecoverServiceSessionAsync(CancellationToken token)
    {
        await using var lifetime = await NetworkHelperConnection.ConnectAsync(token).ConfigureAwait(true);
        await lifetime.Client.RecoverAsync(token).ConfigureAwait(true);
    }

    private async void SystemTunnelToggled(object sender, RoutedEventArgs args)
    {
        if (!SystemTunnelWanted && systemTunnel is { PinnedServices.Count: > 0 })
        {
            preferences.SystemTunnel = true;
            if (!await RestoreNetworkAsync()) return;
            preferences.SystemTunnel = false;
        }
        // Два системных режима сразу не имеют смысла: прокси указывал бы на порт,
        // который уже забрал туннель.
        if (SystemTunnelWanted && preferences.SystemProxy)
        {
            preferences.SystemProxy = false;
            SyncSystemProxy(engine.State);
            UpdateModeText();
        }
        if (!SystemTunnelWanted && SystemTunnelActive) _ = StopSystemTunnelAsync();
        runtime.Zapret.ModeChanged();
        UpdateModeText();
        UpdateSystemTunnelNote(systemTunnel?.State);
        UpdateControls();
        QueueStateSave();
    }

    private Profile[] SystemCandidates(Profile selected) => runtime.Tunnels.Candidates(selected);

    // В системном режиме подбор не трогает ядро напрямую: переключение идёт через сессию,
    // и блокировка остаётся на месте. Останавливать между попытками нечего — неудачная
    // попытка оставляет сессию в удержании, а не возвращает обычную сеть.
    private RouteSearch CreateRouteSearch() => SystemTunnelActive && systemTunnel is { } session
        ? new RouteSearch(session.SwitchAsync, engine.ProbeAsync, static () => { }, FragmentSetter)
        : new RouteSearch(engine, FragmentSetter);

    private async Task StopSystemTunnelAsync()
    {
        var session = systemTunnel;
        systemStopping = true;
        if (!closed) UpdateControls();
        try
        {
            await runtime.StopTunnelAsync().ConfigureAwait(true);
            if (!closed && session?.State == SystemTunnelState.Idle && DetailText.Text == "Отключаем VPN…") DetailText.Text = "VPN отключён.";
        }
        // Прерванная сессия могла уже потерять канал помощника: закрытый канал — тот же отказ.
        catch (Exception ex) when (ex is UserError or IOException or TimeoutException or ObjectDisposedException)
        { if (!closed) DetailText.Text = "Системный режим остановлен не полностью. Проверьте сетевые настройки."; }
        finally
        {
            if (session is not null) session.Changed -= OnSystemTunnelChanged;
            systemStopping = false;
            // Снятая не до конца сессия видна только по журналу: окно должно о ней сказать.
            if (!closed) { UpdateSystemTunnelNote(null); UpdateControls(); }
        }
    }

    private void OnSystemTunnelChanged(SystemTunnelState state)
    {
        if (closed) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (closed) return;
            UpdateSystemTunnelNote(state);
            UpdateControls();
        }));
    }

    private void UpdateSystemTunnelNote(SystemTunnelState? state)
    {
        // Про блокировку говорится только то, что действительно сделано: решает это
        // помощник, а не окно. Без службы фильтрации и брандмауэра блокировать нечем.
        var running = systemTunnel?.BlocksBypass == true
            ? "Системный режим работает: весь трафик компьютера идёт через туннель, мимо него трафик заблокирован."
            : "Системный режим работает: весь трафик компьютера идёт через туннель. Блокировка трафика мимо туннеля " +
              "не действует — фильтрация Windows недоступна, при обрыве программы пойдут напрямую.";
        var leftover = systemTunnel is null && Settings.SystemTunnel.IsEnabled && NetworkLeftChanged;
        Settings.RestoreNetwork.Visibility = Settings.SystemTunnel.IsEnabled &&
            (systemTunnel is null || systemTunnel.PinnedServices.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
        if (leftover)
        {
            Settings.SystemTunnelNote.Text = "Прошлый системный режим прервался, и блокировка трафика могла остаться: " +
                "интернет может быть закрыт. Нажмите «Восстановить сеть» или включите системный режим снова.";
            return;
        }
        Settings.SystemTunnelNote.Text = state switch
        {
            SystemTunnelState.Starting => "Системный режим запускается.",
            SystemTunnelState.Running when runtime.Connections.MainPaused => "Основной VPN выключен. Закреплённые выходы и блокировка обхода продолжают работать.",
            SystemTunnelState.Running => running,
            SystemTunnelState.Holding => systemTunnel?.BlocksBypass == true
                ? "Связь с сервером прервана, восстанавливаем. Трафик мимо туннеля по-прежнему заблокирован: интернета не будет, пока связь не вернётся или вы не нажмёте «Остановить»."
                : "Связь с сервером прервана, восстанавливаем. Интернета не будет, пока связь не вернётся или вы не нажмёте «Остановить». Блокировка трафика мимо туннеля не действует.",
            SystemTunnelState.Stopping => "Системный режим останавливается.",
            SystemTunnelState.RecoveryRequired =>
                "Системный режим остановлен не полностью. Нажмите «Остановить», затем «Восстановить сеть».",
            SystemTunnelState.Failed => "Системный режим прерван. Подключение остановлено.",
            // Граница, о которой обязано сказать пояснение. Без службы блокировка держится,
            // пока работает приложение; со службой — и после его аварийного завершения.
            _ => NetworkHelperConnection.ServiceRunning
                ? "Весь трафик компьютера пойдёт через туннель, а мимо туннеля будет заблокирован фильтрами Windows. Если приложение завершится аварийно, блокировка останется до его следующего запуска."
                : "Весь трафик компьютера пойдёт через туннель, а мимо туннеля будет заблокирован фильтрами Windows. Блокировка действует, пока приложение работает: при его завершении она снимается вместе с режимом."
        };
    }

    private async Task CloseSystemTunnelAsync()
    {
        if (systemTunnel is not null || helperLifetime is not null) await StopSystemTunnelAsync().ConfigureAwait(true);
    }
}
