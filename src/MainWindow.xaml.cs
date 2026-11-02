using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace Oblachka;

public partial class MainWindow : Window
{
    private readonly ClientRuntime runtime;
    private readonly bool ownsRuntime;
    private ITunnelController engine => runtime.Engine;
    private Profile? profile { get => runtime.Profiles.Selected; set => runtime.Profiles.Selected = value; }
    private CancellationTokenSource? operation { get => runtime.Activity.Operation; set => runtime.Activity.Operation = value; }
    private bool closed;
    private bool manualStop { get => runtime.Activity.ManualStop; set => runtime.Activity.ManualStop = value; }
    private bool engineOperation { get => runtime.Activity.EngineOperation; set => runtime.Activity.EngineOperation = value; }
    // Адреса подписки сервиса (основной и резервные) и адреса сторонней подписки.
    // Разделены, чтобы загрузка одной не стирала адрес другой.
    // Загруженная подписка, отложенная до остановки подключения.
    private (ProfileGroup Group, ImportResult Result)? pendingImport;
    private IReadOnlyList<RouteAttempt> routeAttempts = Array.Empty<RouteAttempt>();
    private RecoveryPolicy recovery => runtime.Monitor.Recovery;
    private CancellationTokenSource? recoveryWait { get => runtime.Activity.RecoveryWait; set => runtime.Activity.RecoveryWait = value; }
    private readonly List<ClientEvent> events = new();
    private Forms.NotifyIcon tray => runtime.Tray!.Icon;
    private Forms.ToolStripMenuItem trayConnect => runtime.Tray!.Connect;
    private Forms.ToolStripMenuItem trayStop => runtime.Tray!.Stop;
    public MainWindow(bool persistenceEnabled = true, ClientStateStore? isolatedStore = null)
        : this(new ClientRuntime(), true, persistenceEnabled, isolatedStore) { }
    internal MainWindow(ClientRuntime runtime, bool ownsRuntime = false, bool persistenceEnabled = true, ClientStateStore? isolatedStore = null)
    {
        this.runtime = runtime; this.ownsRuntime = ownsRuntime;
        runtime.Persistence.Configure(isolatedStore ?? (persistenceEnabled ? new ClientStateStore(ClientDataPaths.StateFile) : null), isolatedStore is not null);
        isolatedPersistence = runtime.Persistence.Isolated;
        ThemeManager.EnsureLoaded();
        InitializeComponent();
        // Бывшие настройки «Подключения» живут в разделах острова (Views.SettingsPanel).
        Settings.LiveView = Live; Settings.ServersView = Servers;
        BindPreferences();
        runtime.EnsureTray(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher)).Attach(this);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        engine.Changed += EngineChanged;
        runtime.Persistence.Changed += PersistenceChanged;
        WireViews();
        viewsReady = true;
        runtime.Connections.Changed += ConnectionChanged;
        Navigate("Connection");
        presentationTimer.Tick += (_, _) => { RefreshPresentation(background: true); PollSubscription(); PollPing(); PollZapretRouting(); PollZapretWatch(); PollPause(); PollExpiry(); };
        presentationTimer.Start();
        SubscribeLifecycle(persistenceEnabled && !isolatedPersistence);
        if (stateStore is not null) runtime.Persistence.Start(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));
        InitSystemProxy(persistenceEnabled && !isolatedPersistence);
        InitSystemTunnel(persistenceEnabled && !isolatedPersistence);
        InitStartup(persistenceEnabled && !isolatedPersistence);
        InitLaunch();
        InitHotkey();
        InitBackup();
        Settings.VersionText.Text = BuildIdentity.Label;
        // Идентификатор установки виден пользователю: по нему сервис находит устройство
        // в списке, когда надо освободить место в лимите.
        // Коротко «a7f3…0c21»: целиком номер виден в подсказке и в отчёте.
        var device = DeviceIdentity.Current;
        Settings.DeviceIdText.Text = device.Length > 10 ? device[..4] + "…" + device[^4..] : device;
        Settings.DeviceIdText.ToolTip = device;
        Closing += OnClosing;
        Loaded += (_, _) => { tray.Visible = true; RestoreSavedState(); UpdateControls(); _ = ConnectAtLaunchAsync(); };
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) lastWindowState = WindowState; };
    }
    private void EngineChanged(ConnectionState state, string message)
    {
        if (closed) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (closed) return;
            if (events.Count >= 100) events.RemoveAt(0);
            events.Add(new(DateTimeOffset.Now, (ClientEventCode)(int)state));
            Diagnostics.EventList.ItemsSource = events.AsEnumerable().Reverse().Take(12).ToArray();
            state = engine.State;
            StatusText.Text = state switch
            {
                ConnectionState.Idle => "Отключено", ConnectionState.Validating => "Проверка профиля",
                ConnectionState.Starting => "Запуск ядра", ConnectionState.Running => SystemTunnelActive ? "VPN включён" : "Прокси запущен",
                ConnectionState.Verified => "Туннель проверен", _ => "Подключение прервано"
            };
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, state switch
            { ConnectionState.Verified => "Ok", ConnectionState.Failed => "EI", ConnectionState.Running => "WI", _ => "Fa" });
            EventText.Text = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            SyncSystemProxy(state);
            UpdateControls();
            if (state == ConnectionState.Failed) ScheduleRecovery();
        }));
    }
    private void UpdateControls()
    {
        if (pendingImport is not null && operation is null && engine.State == ConnectionState.Idle)
        {
            var pending = pendingImport.Value; pendingImport = null;
            // Подписку убрали, пока её обновление ждало отключения, — не возвращать.
            if (!removedSubscriptions.Contains(pending.Group.Id)) { ApplyGroupImport(pending.Group, pending.Result, true); return; }
        }
        var active = engine.State is not ConnectionState.Idle;
        ConnectButton.IsEnabled = profile is not null && !active && operation is null && !runtime.Connections.IsStopping;
        // Пока системный режим снимается, повторное нажатие ничего не ускорит.
        StopButton.IsEnabled = (active || operation is not null) && !systemStopping;
        // Добавлять можно и при подключении: подписка применится после остановки (pendingImport),
        // ключ попадёт во «Вручную» и сразу выбран — как и после QR-кода.
        ImportFileButton.IsEnabled = Servers.Add.AddKeyButton.IsEnabled = operation is null;
        Servers.Add.AddKeyButton.IsEnabled &= Servers.Add.KeyInput.Text.Trim().Length > 0;
        RefreshButton.IsEnabled = (groups.Any(g => g.IsSubscription) || pendingImport is not null) && operation is null;
        // Подбор переключает узел, поэтому доступен только внутри своего сервиса.
        FindRouteButton.IsEnabled = profile is { IsService: true } && operation is null;
        // Сервер можно сменить и при работающем подключении: выбор сразу переключает на него.
        // Список закрыт только на время подбора: он сам выбирает сервер. Выбор во время запуска
        // или проверки запоминается и выполняется, как только они закончатся.
        ProfilePicker.IsEnabled = !findingRoute;
        ProbeButton.IsEnabled = engine.ProcessId is not null && operation is null;
        ProbeUrl.IsEnabled = operation is null;
        CopyEndpointButton.IsEnabled = engine.ProcessId is not null;
        ReconnectButton.IsEnabled = profile is not null && operation is null;
        Servers.MeasureButton.IsEnabled = profile is not null && operation is null && importedProfiles.Any(p => p.IsService);
        // Во время подбора его кнопка — «Остановить», а отдельный замер ждёт.
        Servers.BestButton.IsEnabled = tuning is not null || operation is null && importedProfiles.Count > 0;
        if (tuning is not null) Servers.MeasureButton.IsEnabled = false;
        Diagnostics.ValidateRulesButton.IsEnabled = profile is not null && operation is null;
        trayConnect.Enabled = ConnectButton.IsEnabled;
        trayStop.Enabled = StopButton.IsEnabled;
        tray.Text = ServiceBrand.Name + " — " + StatusText.Text;
        EndpointText.Text = engine.ProcessId is not null ? $"socks5://127.0.0.1:{engine.Port}" : "после подключения";
        RefreshPresentation();
    }
    private async void ProbeClick(object sender, RoutedEventArgs args)
    {
        if (vmTestActive || operation is not null) return;
        if (!Uri.TryCreate(ProbeUrl.Text.Trim(), UriKind.Absolute, out var target) || target.Scheme != "https" || !string.IsNullOrEmpty(target.UserInfo))
        { DetailText.Text = "Укажите HTTPS-адрес без логина и пароля."; return; }
        operation = new CancellationTokenSource();
        engineOperation = true;
        UpdateControls();
        DetailText.Text = "Выполняем запрос через туннель…";
        try { var result = await engine.ProbeAsync(target, operation.Token); if (!closed) { RecordProbe(result); DetailText.Text = result.Message; } }
        catch (OperationCanceledException) { engine.Stop(); if (!closed) DetailText.Text = "Проверка отменена, подключение остановлено."; }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        finally { if (manualStop) engine.Stop(); operation.Dispose(); operation = null; engineOperation = false; if (!closed) UpdateControls(); }
    }
    private async void FindRouteClick(object sender, RoutedEventArgs args)
    {
        if (vmTestActive || operation is not null || profile is null) return;
        if (!Uri.TryCreate(ProbeUrl.Text.Trim(), UriKind.Absolute, out var target) || target.Scheme != "https" || target.UserInfo.Length > 0)
        { DetailText.Text = "Укажите HTTPS-адрес проверки без логина и пароля."; return; }
        manualStop = false; recoveryWait?.Cancel(); recovery.Reset();
        operation = new CancellationTokenSource(); engineOperation = true; findingRoute = true; Navigate("Connection"); UpdateControls();
        try
        {
            var profiles = importedProfiles.ToArray();
            // Перебор ограничен узлами сервиса: см. RouteCandidates.
            var candidates = RouteCandidates.For(profiles, profile, pool: InAutoPool);
            var result = await CreateRouteSearch().FindAsync(candidates.Select(i => profiles[i]).ToArray(),
                RouteCandidates.StartAt(candidates, profiles, profile), target, operation.Token,
                (attempt, total) => { if (!closed) DetailText.Text = $"Проверяем подключение {attempt} из {total}. «Остановить» отменяет подбор."; });
            if (closed) return;
            // Нумерация попыток возвращается к позициям в общем списке: отчёт диагностики
            // должен ссылаться на то, что видит пользователь.
            routeAttempts = result.Attempts.Select(a => a with { ProfileIndex = candidates[a.ProfileIndex] }).ToArray();
            if (result.ProfileIndex is int found)
            {
                var index = candidates[found];
                Servers.SearchBox.Clear(); ProfilePicker.ItemsSource = importedProfiles;
                ProfilePicker.SelectedIndex = index; profile = profiles[index]; runningProfile = profile; ShowProfile();
                RecordProbe(new ProbeResult(true, result.Attempts[^1].Milliseconds, "Запрос выполнен"));
                DetailText.Text = $"Работающее подключение найдено. Попыток: {result.Attempts.Count}.";
            }
            else DetailText.Text = $"Успешного запроса нет: проверено {result.Attempts.Count} подключений. " +
                (SystemTunnelActive ? "Блокировка остаётся; «Остановить» вернёт обычную сеть." : "Подключение остановлено.") +
                " Проверьте сеть и сайт проверки; это не доказывает блокировку всех узлов.";
        }
        catch (OperationCanceledException) { if (!closed) DetailText.Text = "Подбор отменён, подключение остановлено."; }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        finally { findingRoute = false; if (manualStop) engine.Stop(); operation.Dispose(); operation = null; engineOperation = false; if (!closed) UpdateControls(); }
    }
    private void CopyEndpointClick(object sender, RoutedEventArgs args)
    {
        if (engine.ProcessId is null) return;
        try { Clipboard.SetText($"socks5://127.0.0.1:{engine.Port}"); DetailText.Text = "Адрес локального SOCKS скопирован."; }
        catch (System.Runtime.InteropServices.ExternalException) { DetailText.Text = "Буфер обмена занят другим приложением. Попробуйте снова."; }
    }
    private async void ReconnectClick(object sender, RoutedEventArgs args)
    {
        if (vmTestActive || operation is not null || profile is null) return;
        recoveryWait?.Cancel(); recovery.Reset(); manualStop = false;
        engine.Stop(); await ConnectCoreAsync();
    }
    private WindowState lastWindowState = WindowState.Normal;
    private bool exitTunnelClosing;
    private async Task CloseAfterTunnelAsync()
    {
        manualStop = true; recoveryWait?.Cancel(); operation?.Cancel();
        StatusText.Text = "Отключаемся…"; DetailText.Text = "Отключаем VPN перед выходом…";
        await Task.WhenAny(CloseSystemTunnelAsync(), Task.Delay(TimeSpan.FromSeconds(30)));
        Close();
    }
    private void ShowWindow() { if (closed) return; if (WindowState == WindowState.Minimized) WindowState = lastWindowState; Show(); Activate(); }
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        FlushStateSave();
        if (!exitRequested && preferences.StayInTray && !Application.Current.Dispatcher.HasShutdownStarted)
        { args.Cancel = true; HideToTray(); return; }
        // Штатный выход возвращает обычную сеть: служба потерю окна считает аварией и держит защиту.
        // При выходе из сеанса Windows ждать некогда — тогда сессию снимает служба (NetworkService).
        if (!exitTunnelClosing && !Application.Current.Dispatcher.HasShutdownStarted && (systemTunnel is not null || helperLifetime is not null))
        { args.Cancel = true; exitTunnelClosing = true; _ = CloseAfterTunnelAsync(); return; }
        closed = true;
        StopZapretOnExit();
        try { vmTestProcess?.CloseMainWindow(); } catch (InvalidOperationException) { }
        presentationTimer.Stop();
        ReleaseViewResources();
        UnsubscribeLifecycle();
        trayPopup?.Close();
        engine.Changed -= EngineChanged;
        if (!runtime.Connections.IsActive) operation?.Cancel();
        recoveryWait?.Cancel();
        ProfilePicker.SelectionChanged -= ProfileSelectionChanged;
        runtime.Tray?.Detach(this);
        if (ownsRuntime) runtime.Dispose();
        pendingImport = null;
    }
}
