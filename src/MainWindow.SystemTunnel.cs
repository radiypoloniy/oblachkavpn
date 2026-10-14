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
    private SystemTunnelSession? systemTunnel;
    private IHelperLifetime? helperLifetime;
    private bool systemStopping;

    private bool SystemTunnelWanted => Settings.SystemTunnel.IsChecked == true;
    private bool SystemTunnelActive => systemTunnel is not null &&
        systemTunnel.State is not (SystemTunnelState.Idle or SystemTunnelState.Failed);

    private void InitSystemTunnel(bool enabled)
    {
        Settings.SystemTunnel.Click += SystemTunnelToggled;
        Settings.RestoreNetwork.Click += async (_, _) => await RestoreNetworkAsync();
        if (!enabled) { Settings.SystemTunnel.IsEnabled = false; return; }
        // Установленная служба — значит, системный VPN готов к работе без запроса прав: он и
        // включён по умолчанию. Сохранённый выбор пользователя применяется позже, при загрузке.
        var service = NetworkHelperConnection.ServiceRunning;
        Settings.SystemTunnel.IsChecked = service;
        if (service) Settings.SystemProxy.IsChecked = false;
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

    private async Task RestoreNetworkAsync()
    {
        if (systemTunnel is not null || operation is not null) return;
        Settings.RestoreNetwork.IsEnabled = false;
        var service = NetworkHelperConnection.ServiceRunning;
        DetailText.Text = service ? "Восстанавливаем сеть." : "Восстанавливаем сеть. Подтвердите запрос прав администратора.";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            if (service) await RecoverServiceSessionAsync(timeout.Token).ConfigureAwait(true);
            else
            {
                // Помощник с UAC восстанавливает сеть по журналу при своём запуске.
                var lifetime = await HelperProcessLifetime.ConnectAsync(NetworkHelperLauncher.Launch, timeout.Token).ConfigureAwait(true);
                await lifetime.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is UserError or IOException or TimeoutException or OperationCanceledException or ObjectDisposedException) { }
        finally { Settings.RestoreNetwork.IsEnabled = true; }
        DetailText.Text = NetworkLeftChanged
            ? "Сеть восстановить не удалось. Перезагрузите компьютер: фильтры защиты после перезагрузки не сохраняются."
            : "Сеть восстановлена.";
        UpdateSystemTunnelNote(null);
    }

    private static async Task RecoverServiceSessionAsync(CancellationToken token)
    {
        await using var lifetime = await NetworkHelperConnection.ConnectAsync(token).ConfigureAwait(true);
        await lifetime.Client.RecoverAsync(token).ConfigureAwait(true);
    }

    private void SystemTunnelToggled(object sender, RoutedEventArgs args)
    {
        // Два системных режима сразу не имеют смысла: прокси указывал бы на порт,
        // который уже забрал туннель.
        if (SystemTunnelWanted && Settings.SystemProxy.IsChecked == true)
        {
            Settings.SystemProxy.IsChecked = false;
            SyncSystemProxy(engine.State);
            UpdateModeText();
        }
        if (!SystemTunnelWanted && SystemTunnelActive) _ = StopSystemTunnelAsync();
        UpdateModeText();
        UpdateSystemTunnelNote(systemTunnel?.State);
        UpdateControls();
        QueueStateSave();
    }

    private async Task StartSystemTunnelAsync(CancellationToken token)
    {
        if (profile is null) throw new UserError("Сначала выберите сервер.");
        if (ForeignTunnel.Find() is { } foreign) { foreignVpn = foreign; throw new UserError(ForeignTunnel.Explain(foreign)); }
        foreignVpn = null;
        // Прерванная сессия (помощник потерян) ещё держит свои объекты. Новая не должна
        // их перезаписать: иначе прежний помощник остался бы без владельца.
        if (systemTunnel is not null || helperLifetime is not null) await StopSystemTunnelAsync().ConfigureAwait(true);
        var service = NetworkHelperConnection.ServiceRunning;
        DetailText.Text = service ? "Готовим системный режим." : "Готовим системный режим. Подтвердите запрос прав администратора.";
        // Сессия, оставленная аварийно завершённым окном, держит сеть закрытой, и адреса
        // узлов не разрешить. Её снимают до подготовки, затем ставят новую.
        if (service && SessionLeft(NetworkService.JournalPath)) await RecoverServiceSessionAsync(token).ConfigureAwait(true);
        var plans = await TunnelEndpoints.PrepareSetAsync(SystemCandidates(profile), token).ConfigureAwait(true);
        helperLifetime = await NetworkHelperConnection.ConnectAsync(token).ConfigureAwait(true);
        var session = new SystemTunnelSession(engine, helperLifetime.Client);
        session.Changed += OnSystemTunnelChanged;
        systemTunnel = session;
        // Правила сайтов и программ исполняет ядро туннеля у помощника; ядро окна в этом
        // режиме правил не несёт (Engine.SiteRules пуст), иначе «напрямую» шло бы из него.
        await session.StartWithSparesAsync(plans, token, siteRules.ToArray(), programRules.ToArray()).ConfigureAwait(true);
    }

    // Выбранный узел первым, затем запасные в том порядке, в каком их переберёт подбор.
    private Profile[] SystemCandidates(Profile selected)
    {
        var profiles = importedProfiles.ToArray();
        var candidates = RouteCandidates.For(profiles, selected);
        if (candidates.Length == 0) return new[] { selected };
        // Сначала порядок автоподбора (выбранный и пять запасных), затем остальные узлы сервиса:
        // на них переключаются вручную, и защита должна разрешать их с самого начала. Иначе
        // смена сервера на такой узел снимала блокировку на время переподключения.
        // Лишнее по лимиту адресов отбросит TunnelEndpoints.PrepareSetAsync.
        return RouteSearch.Order(candidates.Length, RouteCandidates.StartAt(candidates, profiles, selected))
            .Select(i => profiles[candidates[i]])
            .Concat(candidates.Select(i => profiles[i]))
            .DistinctBy(ProfileSelection.Identity).ToArray();
    }

    // В системном режиме подбор не трогает ядро напрямую: переключение идёт через сессию,
    // и блокировка остаётся на месте. Останавливать между попытками нечего — неудачная
    // попытка оставляет сессию в удержании, а не возвращает обычную сеть.
    private RouteSearch CreateRouteSearch() => SystemTunnelActive && systemTunnel is { } session
        ? new RouteSearch(session.SwitchAsync, engine.ProbeAsync, static () => { })
        : new RouteSearch(engine);

    private async Task StopSystemTunnelAsync()
    {
        var session = systemTunnel;
        var lifetime = helperLifetime;
        systemTunnel = null;
        helperLifetime = null;
        if (session is null) return;
        systemStopping = true;
        if (!closed) UpdateControls();
        try
        {
            await session.StopAsync().ConfigureAwait(true);
            if (!closed && session.State == SystemTunnelState.Idle && DetailText.Text == "Отключаем VPN…") DetailText.Text = "VPN отключён.";
        }
        // Прерванная сессия могла уже потерять канал помощника: закрытый канал — тот же отказ.
        catch (Exception ex) when (ex is UserError or IOException or TimeoutException or ObjectDisposedException)
        { if (!closed) DetailText.Text = "Системный режим остановлен не полностью. Проверьте сетевые настройки."; }
        finally
        {
            session.Changed -= OnSystemTunnelChanged;
            systemStopping = false;
            if (lifetime is not null) await lifetime.DisposeAsync().ConfigureAwait(true);
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
        Settings.RestoreNetwork.Visibility = leftover ? Visibility.Visible : Visibility.Collapsed;
        if (leftover)
        {
            Settings.SystemTunnelNote.Text = "Прошлый системный режим прервался, и блокировка трафика могла остаться: " +
                "без неё интернет может не работать. Нажмите «Восстановить сеть» или включите системный режим снова.";
            return;
        }
        Settings.SystemTunnelNote.Text = state switch
        {
            SystemTunnelState.Starting => "Системный режим запускается.",
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
