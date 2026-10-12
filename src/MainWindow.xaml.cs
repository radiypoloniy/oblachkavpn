using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json;
using Forms = System.Windows.Forms;

namespace RoutePilot;

public partial class MainWindow : Window
{
    private readonly ITunnelController engine;
    private Profile? profile;
    private CancellationTokenSource? operation;
    private bool closed;
    private bool manualStop;
    private bool engineOperation;
    private IReadOnlyList<string> subscriptionSources = Array.Empty<string>();
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
            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/RoutePilot;component/Theme.xaml", UriKind.Relative) });
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
        Closing += OnClosing;
        trayConnect = new Forms.ToolStripMenuItem("Подключить");
        trayStop = new Forms.ToolStripMenuItem("Остановить");
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add(trayConnect); menu.Items.Add(trayStop); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Завершить", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        trayConnect.Click += (_, _) => Dispatcher.Invoke(() => ConnectClick(this, new RoutedEventArgs()));
        trayStop.Click += (_, _) => Dispatcher.Invoke(() => StopClick(this, new RoutedEventArgs()));
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Облачка — отключено", ContextMenuStrip = menu };
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
                ConnectionState.Starting => "Запуск ядра", ConnectionState.Running => "Прокси запущен",
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
        StopButton.IsEnabled = active || operation is not null;
        ImportFileButton.IsEnabled = !active && operation is null;
        PasteButton.IsEnabled = !active && operation is null;
        SubscriptionButton.IsEnabled = !active && operation is null;
        RefreshButton.IsEnabled = subscriptionSources.Count > 0 && operation is null;
        FindRouteButton.IsEnabled = profile is not null && operation is null;
        ProfilePicker.IsEnabled = !active && operation is null;
        ProbeButton.IsEnabled = engine.ProcessId is not null && operation is null;
        ProbeUrl.IsEnabled = operation is null;
        CopyEndpointButton.IsEnabled = engine.ProcessId is not null;
        ReconnectButton.IsEnabled = profile is not null && operation is null;
        Servers.MeasureButton.IsEnabled = profile is not null && !active && operation is null;
        Servers.BestButton.IsEnabled = Servers.MeasureButton.IsEnabled;
        Routes.ValidateRulesButton.IsEnabled = profile is not null && operation is null;
        trayConnect.Enabled = ConnectButton.IsEnabled;
        trayStop.Enabled = StopButton.IsEnabled;
        tray.Text = "Облачка — " + StatusText.Text;
        EndpointText.Text = engine.ProcessId is not null ? $"SOCKS5  127.0.0.1:{engine.Port}  •  только TCP" : "Адрес SOCKS появится после запуска";
        RefreshPresentation();
    }
    private async void ConnectClick(object sender, RoutedEventArgs args)
    {
        manualStop = false;
        recoveryWait?.Cancel(); recovery.Reset(); health.Reset();
        await ConnectCoreAsync();
    }
    private async Task ConnectCoreAsync()
    {
        if (vmTestActive || profile is null || operation is not null) return;
        manualStop = false;
        operation = new CancellationTokenSource();
        engineOperation = true;
        UpdateControls();
        try
        {
            await engine.StartAsync(profile, operation.Token);
            nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(3);
            if (!closed) DetailText.Text = "Подключение запущено. Проверь туннель в диагностике.";
        }
        catch (OperationCanceledException) { if (!closed) DetailText.Text = "Запуск отменён или истёк срок ожидания. Можно попробовать снова."; }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        finally { if (manualStop) engine.Stop(); operation.Dispose(); operation = null; engineOperation = false; if (!closed) { UpdateControls(); if (engine.State == ConnectionState.Failed) ScheduleRecovery(); } }
    }
    private void StopClick(object sender, RoutedEventArgs args)
    {
        manualStop = true;
        recoveryWait?.Cancel();
        operation?.Cancel();
        // During startup the async owner tears down the engine; do not race process disposal with it.
        if (operation is null || !engineOperation) engine.Stop();
        else if (engine.State is ConnectionState.Running or ConnectionState.Verified) DetailText.Text = "Отменяем проверку…";
        if (operation is null) DetailText.Text = "Подключение остановлено.";
    }
    private async void ProbeClick(object sender, RoutedEventArgs args)
    {
        if (vmTestActive || operation is not null) return;
        if (!Uri.TryCreate(ProbeUrl.Text.Trim(), UriKind.Absolute, out var target) || target.Scheme != "https" || !string.IsNullOrEmpty(target.UserInfo))
        { DetailText.Text = "Укажи HTTPS-адрес без логина и пароля."; return; }
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
        { DetailText.Text = "Укажи HTTPS-адрес проверки без логина и пароля."; return; }
        manualStop = false; recoveryWait?.Cancel(); recovery.Reset();
        operation = new CancellationTokenSource(); engineOperation = true; findingRoute = true; Navigate("Connection"); UpdateControls();
        try
        {
            var profiles = importedProfiles.ToArray();
            var selected = Array.FindIndex(profiles, p => Profiles.Fingerprint(p) == Profiles.Fingerprint(profile));
            var result = await new RouteSearch(engine).FindAsync(profiles, selected, target, operation.Token,
                (attempt, total) => { if (!closed) DetailText.Text = $"Проверяем подключение {attempt} из {total}. «Остановить» отменяет подбор."; });
            if (closed) return;
            routeAttempts = result.Attempts;
            if (result.ProfileIndex is int index)
            {
                Servers.SearchBox.Clear(); ProfilePicker.ItemsSource = importedProfiles;
                ProfilePicker.SelectedIndex = index; profile = profiles[index]; ShowProfile();
                RecordProbe(new ProbeResult(true, result.Attempts[^1].Milliseconds, "Запрос выполнен"));
                DetailText.Text = $"Работающее подключение найдено. Попыток: {result.Attempts.Count}.";
            }
            else DetailText.Text = $"Успешного запроса нет: проверено {result.Attempts.Count} подключений. Подключение остановлено. Проверь сеть и сайт проверки; это не доказывает блокировку всех узлов.";
        }
        catch (OperationCanceledException) { if (!closed) DetailText.Text = "Подбор отменён, подключение остановлено."; }
        catch (UserError ex) { if (!closed) DetailText.Text = ex.Message; }
        finally { findingRoute = false; if (manualStop) engine.Stop(); operation.Dispose(); operation = null; engineOperation = false; if (!closed) UpdateControls(); }
    }
    private void CopyEndpointClick(object sender, RoutedEventArgs args)
    {
        if (engine.ProcessId is null) return;
        try { Clipboard.SetText($"socks5://127.0.0.1:{engine.Port}"); DetailText.Text = "Адрес локального SOCKS скопирован."; }
        catch (System.Runtime.InteropServices.ExternalException) { DetailText.Text = "Буфер обмена занят другим приложением. Попробуй снова."; }
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
        { args.Cancel = true; Hide(); return; }
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
