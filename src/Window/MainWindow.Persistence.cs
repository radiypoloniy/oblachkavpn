using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private ClientStateStore? stateStore => runtime.Persistence.Store;
    private bool stateRestored { get => runtime.Persistence.Restored; set => runtime.Persistence.Restored = value; }
    private bool restoringState { get => runtime.Persistence.Restoring; set => runtime.Persistence.Restoring = value; }
    private bool stateDirty { get => runtime.Persistence.Dirty; set => runtime.Persistence.Dirty = value; }
    private bool rememberConnections { get => runtime.Persistence.Remember; set => runtime.Persistence.Remember = value; }
    private readonly bool isolatedPersistence;

    private void QueueStateSave() => runtime.Persistence.Queue();
    private void FlushStateSave() => runtime.Persistence.Flush();
    private void PersistenceChanged()
    {
        if (closed) return;
        var error = runtime.Persistence.LastError;
        Settings.StorageStatus.Text = error ?? "Подписка и изменения запоминаются автоматически в зашифрованном виде.";
        if (error is not null) DetailText.Text = error;
    }

    private ClientSnapshot CaptureState() => runtime.Persistence.Capture();

    private IEnumerable<ProfileGroup> SavedGroups => groups.Where(g => g.Source != ProfileSource.Free);

    private void SaveStateClick(object sender, RoutedEventArgs args)
    {
        if (stateStore is null || profile is null || operation is not null) return;
        try
        {
            runtime.Persistence.SaveNow();
            Settings.StorageStatus.Text = "Подключения и настройки сохранены для текущей учётной записи Windows.";
            DetailText.Text = "Сохранено на этом компьютере в зашифрованном виде.";
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }

    private void RestoreSavedState()
    {
        if (stateRestored || stateStore is null) return;
        try
        {
            var snapshot = runtime.Persistence.LoadOnce();
            if (snapshot is null) return;
            restoringState = true;
            try { ApplySnapshot(snapshot); } finally { restoringState = false; }
            Settings.StorageStatus.Text = "Подписка восстановлена. Изменения запоминаются автоматически.";
            DetailText.Text = "Подключения восстановлены. Туннель выключен.";
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }

    private void ApplySnapshot(ClientSnapshot snapshot)
    {
        // До подписок: по доменам сервиса окно узнаёт свою подписку и шлёт ей идентификатор устройства.
        foreach (var host in snapshot.ServiceHosts ?? []) ServiceBrand.Learn(host);
        var restored = ClientStateStore.RestoreProfiles(snapshot);
        preferences.StayInTray = snapshot.CloseToTray ?? true; preferences.AutoRecover = snapshot.AutoRecover;
        preferences.ConnectOnLaunch = snapshot.ConnectOnLaunch; wasConnected = snapshot.WasConnected;
        preferences.Notices = snapshot.Notices;
        RestoreNetworks(snapshot);
        RestoreMachines(snapshot);
        preferences.LocalNetwork = snapshot.LocalNetwork;
        preferences.QuicOverTcp = snapshot.QuicOverTcp;
        preferences.CompatibleStack = snapshot.CompatibleStack;
        RestoreAdvanced(snapshot.Advanced);
        RestoreTweaks(snapshot.ServerTweaks);
        RestoreFree(snapshot);
        trafficHistory.Restore(snapshot.TrafficHistory, snapshot.TrafficDay, snapshot.TrafficBytes, snapshot.TrafficHours, DateTime.Now);
        Array.Clear(trafficHours);
        if (snapshot.TrafficHistory is { })
        {
            var today = trafficHistory.Today(DateTime.Now); trafficDay = today.Day; trafficToday = trafficSavedBytes = today.Upload + today.Download;
            for (var hour = 0; hour < 24; hour++) trafficHours[hour] = today.UploadHours[hour] + today.DownloadHours[hour];
        }
        else
        {
            trafficDay = snapshot.TrafficDay ?? ""; trafficToday = trafficSavedBytes = snapshot.TrafficBytes;
            if (snapshot.TrafficHours is { Length: 24 } hours && hours.All(h => h >= 0)) hours.CopyTo(trafficHours, 0);
        }
        preferences.Hotkey = snapshot.Hotkey; ApplyHotkey();
        preferences.AutoRefresh = snapshot.AutoRefresh;
        ThemeManager.Apply(snapshot.Theme ?? ThemeChoice.System);
        preferences.SystemProxy = snapshot.SystemProxy;
        if (Settings.SystemTunnel.IsEnabled) preferences.SystemTunnel = snapshot.SystemTunnel ?? NetworkHelperConnection.ServiceRunning;
        if (preferences.SystemTunnel) preferences.SystemProxy = false;
        UpdateModeText();
        siteRules.Clear(); siteRules.AddRange(snapshot.RouteRules); RefreshRules();
        programRules.Clear(); programRules.AddRange(snapshot.ProgramRules); RefreshRules();
        runtime.TunnelPolicy.ServiceRoutes.Clear();
        runtime.TunnelPolicy.ServiceRoutes.AddRange(snapshot.ServiceRoutes ?? []);
        RestoreRuleSets(snapshot.RuleSets, snapshot.RuleSetName, snapshot.RuleSetServers);
        measurements.Clear(); foreach (var measurement in snapshot.Measurements) measurements[measurement.Fingerprint] = measurement;
        favorites.Clear(); foreach (var favorite in snapshot.Favorites) favorites.Add(favorite);
        hiddenServers.Clear(); foreach (var hidden in snapshot.HiddenServers) hiddenServers.Add(hidden);
        removedSubscriptions.Clear(); foreach (var removed in snapshot.RemovedSubscriptions) removedSubscriptions.Add(removed);
        autoPool.Load(snapshot.AutoPool);
        // Профиль из файла проверяется так же, как присланный сервисом.
        providerRouting = snapshot.ProviderRouting?.Normalized(); useProviderRouting = snapshot.UseProviderRouting;
        preferences.RussiaDirect = snapshot.RussiaDirect;
        autoSelect = snapshot.AutoSelect;
        preferences.UpdateAuto = snapshot.UpdateCheck;
        preferences.UpdatePrerelease = snapshot.UpdatePrerelease;
        fragmentMode = Enum.IsDefined(snapshot.DpiBypass) ? snapshot.DpiBypass : FragmentMode.Auto;
        zapretEnabled = snapshot.ZapretEnabled;
        selectiveVpn = snapshot.SelectiveVpn; Bypass.SelectiveToggle.IsChecked = selectiveVpn;
        zapretChoices.Clear(); foreach (var (net, choice) in snapshot.ZapretChoices ?? new()) zapretChoices[net] = choice;
        zapretVpnGroups.Clear(); zapretVpnGroups.UnionWith(snapshot.ZapretVpnGroups ?? []);
        RefreshZapret(); RestoreZapret();
        RefreshRouting();
        // Группы из снимка; у снимка до 0.11 их выводим: сервис, одна сторонняя подписка или «Вручную».
        groups.Clear(); groupProfiles.Clear(); groupRevision++;
        var legacy = snapshot.Groups is null;
        var restoredGroups = snapshot.Groups ?? ProfileGroups.FromLegacy(snapshot, restored).ToArray();
        foreach (var group in restoredGroups)
            SetGroup(group, restored.Where(p => legacy ? p.IsService == (group.Source == ProfileSource.Service) : p.Group == group.Id).ToArray());
        restored = groups.SelectMany(g => ProfilesOf(g.Id)).ToArray();
        ApplyProfiles(false);
        // Сначала имя вместе с отпечатком, потом только отпечаток: переименование узла в
        // панели выбор не теряет, а две строки с одинаковой конфигурацией не подменяют друг друга.
        var candidates = restored.Where(p => snapshot.SelectedGroup is null || p.Group == snapshot.SelectedGroup);
        var selected = candidates.FirstOrDefault(p => p.Name == snapshot.SelectedName && Profiles.Fingerprint(p) == snapshot.SelectedFingerprint)
            ?? candidates.FirstOrDefault(p => Profiles.Fingerprint(p) == snapshot.SelectedFingerprint);
        if (selected is not null)
        {
            profile = selected; ShowProfile(); FilterProfiles(); UpdateControls();
        }
    }
}
