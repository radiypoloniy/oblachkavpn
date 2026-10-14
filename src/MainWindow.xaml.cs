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
    private readonly ITunnelController engine;
    private Profile? profile;
    private CancellationTokenSource? operation;
    private bool closed;
    private bool manualStop;
    private bool engineOperation;
    // Адреса подписки сервиса (основной и резервные) и адреса сторонней подписки.
    // Разделены, чтобы загрузка одной не стирала адрес другой.
    private IReadOnlyList<string> subscriptionSources = Array.Empty<string>();
    private IReadOnlyList<string> externalSources = Array.Empty<string>();
    private int preferredSource;
    private ImportResult? pendingImport;
    private IReadOnlyList<RouteAttempt> routeAttempts = Array.Empty<RouteAttempt>();
    private readonly RecoveryPolicy recovery = new();
    private CancellationTokenSource? recoveryWait;
    private readonly List<ClientEvent> events = new();
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ToolStripMenuItem trayConnect;
    private readonly Forms.ToolStripMenuItem trayStop;
    public MainWindow(bool persistenceEnabled = true, ClientStateStore? isolatedStore = null)
    {
        stateStore = isolatedStore ?? (persistenceEnabled ? new ClientStateStore(ClientDataPaths.StateFile) : null);
        isolatedPersistence = isolatedStore is not null;
        if (!Application.Current.Resources.Contains("Ink"))
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Oblachka;component/Theme.xaml", UriKind.Relative) });
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        engine = new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), Path.Combine(AppContext.BaseDirectory, "data", "runtime"));
        engine.Changed += EngineChanged;
        WireViews();
        viewsReady = true;
        Navigate("Connection");
        presentationTimer.Tick += (_, _) => { RefreshPresentation(); PollHealth(); PollSubscription(); };
        presentationTimer.Start();
        SubscribeLifecycle(persistenceEnabled && isolatedStore is null);
        InitSystemProxy(persistenceEnabled && isolatedStore is null);
        InitSystemTunnel(persistenceEnabled && isolatedStore is null);
        Settings.VersionText.Text = BuildIdentity.Label;
        Onboarding.BrandText.Text = ServiceBrand.Name;
        // Идентификатор установки виден пользователю: по нему сервис находит устройство
        // в списке, когда надо освободить место в лимите.
        Settings.DeviceIdText.Text = "Идентификатор устройства: " + DeviceIdentity.Current;
        Closing += OnClosing;
        trayConnect = new Forms.ToolStripMenuItem("Подключить");
        trayStop = new Forms.ToolStripMenuItem("Остановить");
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add(trayConnect); menu.Items.Add(trayStop); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Завершить", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        trayConnect.Click += (_, _) => Dispatcher.Invoke(() => ConnectClick(this, new RoutedEventArgs()));
        trayStop.Click += (_, _) => Dispatcher.Invoke(() => StopClick(this, new RoutedEventArgs()));
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = ServiceBrand.Name + " — отключено", ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        tray.MouseUp += (_, args) => { if (args.Button == Forms.MouseButtons.Left && args.Clicks < 2) Dispatcher.Invoke(ShowTrayPopup); };
        Loaded += (_, _) => { tray.Visible = true; RestoreSavedState(); UpdateControls(); };
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
            StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(state switch
            { ConnectionState.Verified => "#276B68", ConnectionState.Failed => "#B24635", ConnectionState.Running => "#A87620", _ => "#8798A7" }));
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
            var pending = pendingImport; pendingImport = null;
            ApplyImport(pending, true);
            return;
        }
        var active = engine.State is not ConnectionState.Idle;
        ConnectButton.IsEnabled = profile is not null && !active && operation is null;
        // Пока системный режим снимается, повторное нажатие ничего не ускорит.
        StopButton.IsEnabled = (active || operation is not null) && !systemStopping;
        ImportFileButton.IsEnabled = !active && operation is null;
        PasteButton.IsEnabled = !active && operation is null;
        SubscriptionButton.IsEnabled = !active && operation is null;
        RefreshButton.IsEnabled = (subscriptionSources.Count > 0 || externalSources.Count > 0) && operation is null;
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
        Servers.BestButton.IsEnabled = Servers.MeasureButton.IsEnabled;
        Routes.ValidateRulesButton.IsEnabled = profile is not null && operation is null;
        trayConnect.Enabled = ConnectButton.IsEnabled;
        trayStop.Enabled = StopButton.IsEnabled;
        tray.Text = ServiceBrand.Name + " — " + StatusText.Text;
        EndpointText.Text = engine.ProcessId is not null ? $"SOCKS5  127.0.0.1:{engine.Port}  •  только TCP" : "Адрес SOCKS появится после запуска";
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
            var candidates = RouteCandidates.For(profiles, profile);
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
    private void ShowWindow() { if (closed) return; if (WindowState == WindowState.Minimized) WindowState = lastWindowState; Show(); Activate(); }
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        FlushStateSave();
        if (!exitRequested && Settings.StayInTray.IsChecked == true && !Application.Current.Dispatcher.HasShutdownStarted)
        { args.Cancel = true; HideToTray(); return; }
        closed = true;
        try { vmTestProcess?.CloseMainWindow(); } catch (InvalidOperationException) { }
        presentationTimer.Stop();
        UnsubscribeLifecycle();
        trayPopup?.Close();
        engine.Changed -= EngineChanged;
        operation?.Cancel();
        recoveryWait?.Cancel();
        engine.Dispose();
        CloseSystemProxy();
        tray.Visible = false; tray.Dispose();
        profile = null;
        subscriptionSources = Array.Empty<string>(); pendingImport = null;
    }
}
