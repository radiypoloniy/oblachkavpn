using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Oblachka.Views;

namespace Oblachka;

// Главный экран нового дизайна (design/redesign-2026-10-14, экраны 05–09): статус, тумблер,
// плашка проверки связи, объявление, быстрый список серверов и режим в подвале.
//
// Логика подключения прежняя: тумблер вызывает те же ConnectClick и StopClick, а состояние
// берётся из движка, системной сессии и последней проверки связи.
public partial class MainWindow
{
    private string? dismissedAnnounce;
    // «Автовыбор»: сервер выбирается по свежим замерам, а при обрыве подбор переключает сам.
    private bool autoSelect;
    private string? quickSignature;
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    private void WireHome()
    {
        Home.DirectChip.Click += (_, _) => OpenSettings(1);
        Home.Power.Click += (_, _) => { if (StopButton.IsEnabled) StopClick(this, new RoutedEventArgs()); else if (ConnectButton.IsEnabled) ConnectClick(this, new RoutedEventArgs()); };
        Home.SettingsButton.Click += SettingsClick;
        Home.AllServersButton.Click += (_, _) => Navigate("Servers");
        Home.ModeButton.Click += (_, _) => ShowModeMenu(ModeMenuLayer.Visibility != Visibility.Visible);
        ModeTunOption.Click += async (_, _) => await SetModeAsync(tunnel: true);
        ModeProxyOption.Click += async (_, _) => await SetModeAsync(tunnel: false);
        Home.AnnounceClose.Click += (_, _) => { dismissedAnnounce = Home.AnnounceText.Text; Home.AnnounceCard.Visibility = Visibility.Collapsed; };
        Home.QuickList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(QuickRowClick));
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(ShellFeedback, (_, _) => ShowToast());
        ThemeManager.Changed += RefreshHome;
        WireStates();
        WireSidebar();
    }

    private string SessionDuration => sessionStarted is null ? "0:00:00" : (DateTimeOffset.UtcNow - sessionStarted.Value).ToString(@"h\:mm\:ss");

    private void RefreshHome()
    {
        if (!viewsReady) return;
        RefreshDirectChip();
        // В системном режиме ядро окна поднимается раньше туннеля: «Защищено» — только когда
        // служба подтвердила туннель, до этого идёт подключение.
        var tunnelStarting = systemTunnel?.State == SystemTunnelState.Starting;
        var running = engine.State is ConnectionState.Running or ConnectionState.Verified && !tunnelStarting;
        var busy = engine.State is ConnectionState.Starting or ConnectionState.Validating || tunnelStarting || operation is not null && engineOperation && !backgroundCheck;
        var holding = systemTunnel?.State == SystemTunnelState.Holding;
        var name = (runningProfile ?? profile)?.DisplayName ?? "";
        var (title, sub, power, dot) =
            systemStopping ? ("Отключаемся…", name, PowerState.Connecting, "MidI")
            : findingRoute || holding ? ("Восстанавливаем связь", name + " не отвечает", PowerState.Connecting, "MidI")
            : busy && !running ? ("Подключаемся…", profile?.DisplayName ?? "", PowerState.Connecting, "MidI")
            // «Защищено» — только когда через туннель идёт весь компьютер. Прокси защищает не всё.
            // Проверка связи не прошла: не «Защищено», а честно — связь пропала, пока не вернётся
            // или подбор не найдёт рабочий сервер.
            : running && health.FailedRounds > 0 ? ("Связь пропала", name + " не отвечает · проверяем ещё раз", PowerState.On, "WI")
            // Выборочный режим не защищает весь компьютер: большая часть трафика идёт напрямую.
            : running ? (SystemTunnelActive && !selectiveVpn ? "Защищено" : "Подключено", name + " · " + SessionDuration, PowerState.On, "Ok")
            : engine.State == ConnectionState.Failed ? ("Связь прервана", "Включите снова или выберите другой сервер", PowerState.Off, "EI")
            // VPN выключен, но обход работает: YouTube и Discord открываются и так — это стоит сказать.
            : ZapretWorksWithoutVpn ? ("VPN выключен", string.Join(" и ", zapretActiveGroups.Select(ZapretTitle)) + (zapretActiveGroups.Length > 1 ? " работают" : " работает") + " и без него", PowerState.Off, "Ok")
            : ("Не подключено", "Нажмите кнопку или выберите сервер", PowerState.Off, "Fa");
        Home.StatusTitle.Text = title;
        Home.StatusSub.Text = sub;
        Home.StatusDot.SetResourceReference(Shape.FillProperty, dot);
        Home.Power.State = profile is null ? PowerState.Disabled : power;

        var checkedOk = running && lastProbe is { Success: true };
        Home.CheckText.Text = checkedOk ? "Связь проверена · " + lastProbe!.Milliseconds + " мс"
            : running && lastProbe is { Success: false } ? "Проверка связи не прошла"
            : running ? "Проверяем связь…"
            : busy ? "Запускаем ядро и проверяем связь" : "Туннель выключен";
        Home.CheckChip.SetResourceReference(Border.BackgroundProperty, checkedOk ? "OkB" : "Field");
        Home.CheckText.SetResourceReference(TextBlock.ForegroundProperty, checkedOk ? "OkF" : "Mu2");
        Home.CheckIcon.Visibility = checkedOk ? Visibility.Visible : Visibility.Collapsed;
        ApplySpecialState(running);

        RefreshSidebar();
        // Список меняется, только если изменилось видимое: иначе ежесекундное обновление
        // перезапускало бы анимацию отметки и сбрасывало наведение.
        var rows = QuickRows();
        var signature = string.Join('\n', rows.Select(r => r.Name + "|" + r.Note + "|" + r.Selected + "|" + r.Latency + "|" + r.Favorite));
        if (signature != quickSignature) { quickSignature = signature; Home.QuickList.ItemsSource = rows; }
    }

    // Автовыбор, затем избранные (без избранного — первые узлы сервиса), до пяти строк.
    // Выбранный сервер, если его нет среди них, подставляется последней строкой.
    private IReadOnlyList<ServerRow> QuickRows()
    {
        var rows = new List<ServerRow> { new()
        {
            IsAuto = true, Name = "Автовыбор", Selected = autoSelect,
            Note = autoSelect && profile is not null ? "сейчас: " + profile.DisplayName : "самый быстрый из пула",
            Icon = TryFindResource("IcoAuto") as Geometry, IconOnAccent = true
        } };
        var favored = importedProfiles.Where(p => favorites.Contains(FavoriteKey(p))).ToList();
        // Без избранного — первые узлы текущей подписки (сервиса или той, где выбран узел).
        if (favored.Count == 0) favored = (CurrentGroup is { } current ? ProfilesOf(current.Id) : importedProfiles).ToList();
        foreach (var item in favored.Take(4)) rows.Add(Row(item));
        if (!autoSelect && profile is not null && !rows.Any(r => r.Profile is { } p && ProfileSelection.Identity(p) == ProfileSelection.Identity(profile)))
        {
            if (rows.Count == 5) rows.RemoveAt(4);
            rows.Add(Row(profile));
        }
        return rows;
    }

    // note: false — строка под заголовком своей подписки, повторять её имя незачем.
    private ServerRow Row(Profile item) => Row(item, true);
    private ServerRow Row(Profile item, bool note)
    {
        var now = DateTimeOffset.UtcNow;
        var measured = LatestMeasurement(item);
        // У работающего подключения свежее всего — последняя проверка связи через него.
        if ((measured is null || !ServerMeasurements.Fresh(measured, now)) && runningProfile is not null && ReferenceEquals(item, runningProfile) && lastProbe is { Success: true })
            measured = new(Profiles.Fingerprint(item), now, true, lastProbe.Milliseconds);
        var (latency, dot, hint) = ServerRow.Status(measured, now, checkingServer is not null && ReferenceEquals(item, checkingServer));
        var icon = ServerRow.IconFor(item);
        return new ServerRow
        {
            Profile = item, Name = item.DisplayName, Note = note ? GroupNote(item) : null,
            Flag = icon is null ? FlagImages.Local(item.Country.Code) : null, Icon = icon, IconMuted = !item.IsService,
            Latency = latency, LatencyDot = dot, LatencyHint = hint, Speed = ServerRow.SpeedText(measured, now),
            Selected = !autoSelect && profile is not null && ProfileSelection.Identity(item) == ProfileSelection.Identity(profile),
            Favorite = favorites.Contains(FavoriteKey(item))
        };
    }

    // Клик по серверу — сразу подключение к нему, и при выключенном VPN тоже.
    private void QuickRowClick(object sender, RoutedEventArgs args)
    {
        if ((args.OriginalSource as FrameworkElement)?.DataContext is not ServerRow row) return;
        PickServer(row.IsAuto ? null : row.Profile);
    }

    private async void PickServer(Profile? chosen)
    {
        autoSelect = chosen is null;
        QueueStateSave();
        // Сразу после запуска отклики ещё идут: подождать их до 3 с лучше, чем взять текущий.
        if (autoSelect) await WaitForPingsAsync();
        var target = chosen ?? BestProfile();
        if (target is not null) SelectProfile(target);
        RefreshHome();
        if (!ConnectionActive && ConnectButton.IsEnabled) ConnectClick(this, new RoutedEventArgs());
    }

    // При «Автовыборе» каждое включение начинается с лучшего по свежим замерам сервера,
    // а не с того, что выбрался в прошлый раз.
    private async Task PickBestBeforeConnectAsync()
    {
        if (!autoSelect || ConnectionActive) return;
        await WaitForPingsAsync();
        if (BestProfile() is { } best && (profile is null || ProfileSelection.Identity(best) != ProfileSelection.Identity(profile))) SelectProfile(best);
    }

    private Profile? BestProfile()
    {
        var all = importedProfiles.ToArray();
        // Только пул автовыбора: серверы обхода белых списков и исключённые вручную не берутся.
        var pool = autoPool.Members(all);
        return ServerMeasurements.Best(pool, EffectiveMeasurements(), DateTimeOffset.UtcNow, profile) is int best ? pool[best]
            : profile is { IsService: true } && autoPool.Contains(profile) ? profile : pool.FirstOrDefault(p => p.IsService) ?? pool.FirstOrDefault() ?? profile;
    }

    // Выбор идёт через прежний список (Servers.ProfilePicker): от его события зависят смена
    // сервера на ходу и сохранение. Поиск сбрасывается, иначе нужной строки в нём может не быть.
    private void SelectProfile(Profile target)
    {
        if (!ReferenceEquals(ProfilePicker.ItemsSource, importedProfiles)) { Servers.SearchBox.Clear(); ProfilePicker.ItemsSource = importedProfiles; }
        ProfilePicker.SelectedItem = target;
    }

    private void ShowModeMenu(bool show)
    {
        UpdateModeText();
        ModeMenuLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        // Появление: сдвиг 6 → 0 и масштаб .98 → 1 за 180 мс.
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        ModeMenuShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        ModeMenuScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        ModeMenuScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        ModeMenu.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void ModeMenuLayerMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs args)
    { if (ReferenceEquals(args.OriginalSource, ModeMenuLayer)) ShowModeMenu(false); }

    // Смена режима при работающем подключении: остановить в прежнем режиме и подключить в новом.
    private async Task SetModeAsync(bool tunnel)
    {
        ShowModeMenu(false);
        if (tunnel == SystemTunnelWanted && (tunnel || Settings.SystemProxy.IsChecked == true)) return;
        if (tunnel && !Settings.SystemTunnel.IsEnabled) { DetailText.Text = "Системный режим недоступен в этой сборке."; return; }
        var reconnect = ConnectionActive;
        if (reconnect)
        {
            manualStop = true; recoveryWait?.Cancel();
            if (systemTunnel is not null) await StopSystemTunnelAsync(); else engine.Stop();
        }
        Settings.SystemTunnel.IsChecked = tunnel;
        Settings.SystemProxy.IsChecked = !tunnel;
        SystemTunnelToggled(this, new RoutedEventArgs());
        SyncSystemProxy(engine.State);
        UpdateModeText();
        // Состояние кнопок после остановки обновляется событием движка асинхронно: здесь оно
        // ещё прежнее. Без явного обновления переход «Браузеры → Весь компьютер» не переподключал.
        UpdateControls();
        if (reconnect && ConnectButton.IsEnabled) await ConnectCoreAsync();
    }

    private static void MarkRadio(Ellipse radio, bool on)
    {
        radio.SetResourceReference(Shape.StrokeProperty, on ? "Ac" : "Ring");
        radio.StrokeThickness = on ? 5 : 1.5;
    }

    private void ShowToast()
    {
        if (string.IsNullOrWhiteSpace(ShellFeedback.Text) || IsRoutine(ShellFeedback.Text)) { Toast.Visibility = Visibility.Collapsed; return; }
        Toast.Visibility = Visibility.Visible;
        toastTimer.Stop(); toastTimer.Start();
    }

    // Ход подключения уже виден в статусе и на тумблере — всплывашка его не дублирует.
    private static readonly string[] RoutinePrefixes = ["Готовим", "Системный режим запущен", "VPN отключён", "Подключено к ", "Подключение запущено",
        "Восстанавливаем сеть", "Проверяем подключение", "Проверено серверов", "Загружаем подписку", "Выполняем запрос"];
    internal static bool IsRoutine(string text) => RoutinePrefixes.Any(p => text.StartsWith(p, StringComparison.Ordinal));

    private void MinimizeClick(object sender, RoutedEventArgs args) => WindowState = WindowState.Minimized;
    private void CloseWindowClick(object sender, RoutedEventArgs args) => Close();
}
