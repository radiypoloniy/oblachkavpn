using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    private readonly ClientStateStore? stateStore;
    private bool stateRestored;
    private bool restoringState;
    private bool stateDirty;
    private bool rememberConnections = true;
    private readonly bool isolatedPersistence;

    private void QueueStateSave()
    {
        // Без выбранного сервера тоже: убранная последняя подписка должна сохраниться пустым списком.
        if (stateStore is not null && !restoringState && rememberConnections && (stateRestored || profile is not null)) stateDirty = true;
    }

    private void FlushStateSave()
    {
        // Отложенное обновление подписки (оно ждёт отключения) сохранению не мешает: сохраняется то,
        // что сейчас в списке. Раньше из-за него за всю подключённую сессию не сохранялось ничего —
        // и убранная подписка возвращалась после перезапуска.
        if (!stateDirty || restoringState || stateStore is null || !rememberConnections || !(stateRestored || profile is not null) || operation is not null) return;
        stateDirty = false;
        try
        {
            stateStore.Save(CaptureState());
            Settings.StorageStatus.Text = "Подписка и изменения запоминаются автоматически в зашифрованном виде.";
        }
        catch (UserError ex) { Settings.StorageStatus.Text = ex.Message; DetailText.Text = ex.Message; }
    }

    private ClientSnapshot CaptureState() => new(1,
        // Все серверы групп, со скрытыми: иначе после перезапуска их нечем было бы вернуть.
        // Бесплатные чужие узлы (FreeServers) не сохраняются: после перезапуска их нет.
        SavedGroups.SelectMany(g => ProfilesOf(g.Id)).Select(p => new SavedProfile(p.Name, (System.Text.Json.Nodes.JsonObject)p.Outbound.DeepClone()) { Source = p.Source, Group = p.Group }).ToArray(),
        profile is null || profile.IsFree ? null : Profiles.Fingerprint(profile), subscriptionSources.ToArray(), preferredSource,
        Settings.StayInTray.IsChecked == true, AutoRecover.IsChecked == true)
        { SelectedName = profile is { IsFree: false } ? profile.Name : null, ExternalSources = externalSources.ToArray(), AutoRefresh = Settings.AutoRefresh.IsChecked == true, SystemProxy = Settings.SystemProxy.IsChecked == true, CloseToTray = Settings.StayInTray.IsChecked == true, SystemTunnel = Settings.SystemTunnel.IsChecked == true, Theme = ThemeManager.Choice, Metadata = ServiceGroup?.Metadata, Groups = SavedGroups.ToArray(), RouteRules = PersistedSet.Sites, ProgramRules = PersistedSet.Programs,
            Favorites = favorites.Where(f => importedProfiles.Any(p => FavoriteKey(p) == f)).Take(Subscriptions.MaxProfiles).ToArray(),
            HiddenServers = hiddenServers.Take(Subscriptions.MaxProfiles).ToArray(), RemovedSubscriptions = removedSubscriptions.ToArray(),
            Measurements = measurements.Values.Where(m => importedProfiles.Any(p => !p.IsFree && Profiles.Fingerprint(p) == m.Fingerprint)).Take(200).ToArray(),
            ProviderRouting = providerRouting, UseProviderRouting = useProviderRouting, RussiaDirect = Settings.RussiaDirect.IsChecked == true, AutoSelect = autoSelect,
            UpdateCheck = AppTab.UpdateAuto.IsChecked == true, UpdatePrerelease = AppTab.UpdatePrerelease.IsChecked == true, DpiBypass = fragmentMode,
            ZapretEnabled = zapretEnabled, SelectiveVpn = selectiveVpn, ZapretChoices = new Dictionary<string, string>(zapretChoices), ZapretVpnGroups = zapretVpnGroups.Order(StringComparer.Ordinal).ToArray(),
            AutoPool = autoPool.Snapshot(importedProfiles),
            ConnectOnLaunch = Settings.ConnectOnLaunch.IsChecked == true, WasConnected = wasConnected, Notices = Settings.Notices.IsChecked == true, ExpiryNotice = expiryNotice,
            KnownNetworks = knownNetworks.Count == 0 ? null : new(knownNetworks), SecureUnknown = AppTab.SecureUnknown.IsChecked == true, Lockdown = AppTab.Lockdown.IsChecked == true,
            MachinePick = machineMode, MachineMemory = machineMemory.Count == 0 ? null : machineMemory.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value)),
            ServiceHosts = ServiceBrand.LearnedIn(groups.SelectMany(g => g.Sources).Concat(subscriptionSources)), LocalNetwork = Settings.LocalNetwork.IsChecked == true,
            QuicOverTcp = Settings.QuicOverTcp.IsChecked == true, CompatibleStack = Settings.CompatibleStack.IsChecked == true, Advanced = advanced.IsDefault ? null : advanced, ServerTweaks = serverTweaks.Count == 0 ? null : new(serverTweaks),
            RuleSets = ruleSets.Count == 0 ? null : SavedRuleSets().Where(s => s.Name != PersistedSet.Name).ToArray(), RuleSetName = ruleSets.Count == 0 && PersistedSet.Name == MainRuleSet ? null : PersistedSet.Name,
            RuleSetServers = PersistedSet.Servers, TrafficDay = trafficDay, TrafficBytes = trafficToday, TrafficHours = trafficHours.ToArray(), TrafficHistory = trafficHistory.Snapshot(DateTime.Now),
            Hotkey = Settings.Hotkey.IsChecked == true, FreeConsent = freeConsent, FreeList = freeList.ToString() };

    private IEnumerable<ProfileGroup> SavedGroups => groups.Where(g => g.Source != ProfileSource.Free);

    private void SaveStateClick(object sender, RoutedEventArgs args)
    {
        if (stateStore is null || profile is null || operation is not null) return;
        try
        {
            stateStore.Save(CaptureState());
            stateDirty = false;
            Settings.StorageStatus.Text = "Подключения и настройки сохранены для текущей учётной записи Windows.";
            DetailText.Text = "Сохранено на этом компьютере в зашифрованном виде.";
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }

    private void RestoreSavedState()
    {
        if (stateRestored || stateStore is null) return;
        stateRestored = true;
        try
        {
            var snapshot = stateStore.LoadWithMigration(isolatedPersistence ? Array.Empty<string>() : ClientDataPaths.LegacyFiles);
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
        Settings.StayInTray.IsChecked = snapshot.CloseToTray ?? true; AutoRecover.IsChecked = snapshot.AutoRecover;
        Settings.ConnectOnLaunch.IsChecked = snapshot.ConnectOnLaunch; wasConnected = snapshot.WasConnected;
        Settings.Notices.IsChecked = snapshot.Notices; expiryNotice = snapshot.ExpiryNotice;
        RestoreNetworks(snapshot);
        RestoreMachines(snapshot);
        Settings.LocalNetwork.IsChecked = snapshot.LocalNetwork;
        Settings.QuicOverTcp.IsChecked = snapshot.QuicOverTcp;
        Settings.CompatibleStack.IsChecked = snapshot.CompatibleStack;
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
        Settings.Hotkey.IsChecked = snapshot.Hotkey; ApplyHotkey();
        Settings.AutoRefresh.IsChecked = snapshot.AutoRefresh;
        ThemeManager.Apply(snapshot.Theme ?? ThemeChoice.System);
        Settings.SystemProxy.IsChecked = snapshot.SystemProxy;
        if (Settings.SystemTunnel.IsEnabled) Settings.SystemTunnel.IsChecked = snapshot.SystemTunnel ?? NetworkHelperConnection.ServiceRunning;
        if (Settings.SystemTunnel.IsChecked == true) Settings.SystemProxy.IsChecked = false;
        UpdateModeText();
        siteRules.Clear(); siteRules.AddRange(snapshot.RouteRules); RefreshRules();
        programRules.Clear(); programRules.AddRange(snapshot.ProgramRules); RefreshRules();
        RestoreRuleSets(snapshot.RuleSets, snapshot.RuleSetName, snapshot.RuleSetServers);
        measurements.Clear(); foreach (var measurement in snapshot.Measurements) measurements[measurement.Fingerprint] = measurement;
        favorites.Clear(); foreach (var favorite in snapshot.Favorites) favorites.Add(favorite);
        hiddenServers.Clear(); foreach (var hidden in snapshot.HiddenServers) hiddenServers.Add(hidden);
        removedSubscriptions.Clear(); foreach (var removed in snapshot.RemovedSubscriptions) removedSubscriptions.Add(removed);
        autoPool.Load(snapshot.AutoPool);
        // Профиль из файла проверяется так же, как присланный сервисом.
        providerRouting = snapshot.ProviderRouting?.Normalized(); useProviderRouting = snapshot.UseProviderRouting;
        Settings.RussiaDirect.IsChecked = snapshot.RussiaDirect;
        autoSelect = snapshot.AutoSelect;
        AppTab.UpdateAuto.IsChecked = snapshot.UpdateCheck;
        AppTab.UpdatePrerelease.IsChecked = snapshot.UpdatePrerelease;
        fragmentMode = Enum.IsDefined(snapshot.DpiBypass) ? snapshot.DpiBypass : FragmentMode.Auto;
        zapretEnabled = snapshot.ZapretEnabled;
        selectiveVpn = snapshot.SelectiveVpn; Bypass.SelectiveToggle.IsChecked = selectiveVpn;
        zapretChoices.Clear(); foreach (var (net, choice) in snapshot.ZapretChoices ?? new()) zapretChoices[net] = choice;
        zapretVpnGroups.Clear(); zapretVpnGroups.UnionWith(snapshot.ZapretVpnGroups ?? []);
        RefreshZapret(); RestoreZapret();
        RefreshRouting();
        // Группы из снимка; у снимка до 0.11 их выводим: сервис, одна сторонняя подписка или «Вручную».
        groups.Clear(); groupProfiles.Clear();
        var legacy = snapshot.Groups is null;
        var restoredGroups = snapshot.Groups ?? ProfileGroups.FromLegacy(snapshot, restored).ToArray();
        foreach (var group in restoredGroups)
            SetGroup(group, restored.Where(p => legacy ? p.IsService == (group.Source == ProfileSource.Service) : p.Group == group.Id).ToArray());
        restored = groups.SelectMany(g => ProfilesOf(g.Id)).ToArray();
        ApplyProfiles(false);
        // Сначала имя вместе с отпечатком, потом только отпечаток: переименование узла в
        // панели выбор не теряет, а две строки с одинаковой конфигурацией не подменяют друг друга.
        var selected = restored.FirstOrDefault(p => p.Name == snapshot.SelectedName && Profiles.Fingerprint(p) == snapshot.SelectedFingerprint)
            ?? restored.FirstOrDefault(p => Profiles.Fingerprint(p) == snapshot.SelectedFingerprint);
        if (selected is not null) ProfilePicker.SelectedItem = selected;
    }
}
