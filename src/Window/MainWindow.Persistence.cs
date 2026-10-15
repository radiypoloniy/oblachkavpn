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
        if (stateStore is not null && !restoringState && rememberConnections && profile is not null) stateDirty = true;
    }

    private void FlushStateSave()
    {
        if (!stateDirty || restoringState || stateStore is null || !rememberConnections || profile is null || operation is not null || pendingImport is not null) return;
        stateDirty = false;
        try
        {
            stateStore.Save(CaptureState());
            Settings.StorageStatus.Text = "Подписка и изменения запоминаются автоматически в зашифрованном виде.";
        }
        catch (UserError ex) { Settings.StorageStatus.Text = ex.Message; DetailText.Text = ex.Message; }
    }

    private ClientSnapshot CaptureState() => new(1,
        importedProfiles.Select(p => new SavedProfile(p.Name, (System.Text.Json.Nodes.JsonObject)p.Outbound.DeepClone()) { Source = p.Source, Group = p.Group }).ToArray(),
        profile is null ? null : Profiles.Fingerprint(profile), subscriptionSources.ToArray(), preferredSource,
        Settings.StayInTray.IsChecked == true, AutoRecover.IsChecked == true)
        { SelectedName = profile?.Name, ExternalSources = externalSources.ToArray(), AutoRefresh = Settings.AutoRefresh.IsChecked == true, SystemProxy = Settings.SystemProxy.IsChecked == true, CloseToTray = Settings.StayInTray.IsChecked == true, SystemTunnel = Settings.SystemTunnel.IsChecked == true, Theme = ThemeManager.Choice, Metadata = ServiceGroup?.Metadata, Groups = groups.ToArray(), RouteRules = siteRules.ToArray(), ProgramRules = programRules.ToArray(),
            Favorites = favorites.Where(f => importedProfiles.Any(p => FavoriteKey(p) == f)).Take(Subscriptions.MaxProfiles).ToArray(),
            Measurements = measurements.Values.Where(m => importedProfiles.Any(p => Profiles.Fingerprint(p) == m.Fingerprint)).Take(200).ToArray(),
            ProviderRouting = providerRouting, UseProviderRouting = useProviderRouting, RussiaDirect = Routes.RussiaDirect.IsChecked == true, AutoSelect = autoSelect,
            UpdateCheck = Diagnostics.UpdateAuto.IsChecked == true };

    private void SaveStateClick(object sender, RoutedEventArgs args)
    {
        if (stateStore is null || profile is null || operation is not null) return;
        if (pendingImport is not null) { DetailText.Text = "Сначала остановите подключение, чтобы сохранить обновлённый список."; return; }
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
        var restored = ClientStateStore.RestoreProfiles(snapshot);
        Settings.StayInTray.IsChecked = snapshot.CloseToTray ?? true; AutoRecover.IsChecked = snapshot.AutoRecover;
        Settings.AutoRefresh.IsChecked = snapshot.AutoRefresh;
        ThemeManager.Apply(snapshot.Theme ?? ThemeChoice.System);
        Settings.SystemProxy.IsChecked = snapshot.SystemProxy;
        if (Settings.SystemTunnel.IsEnabled) Settings.SystemTunnel.IsChecked = snapshot.SystemTunnel ?? NetworkHelperConnection.ServiceRunning;
        if (Settings.SystemTunnel.IsChecked == true) Settings.SystemProxy.IsChecked = false;
        UpdateModeText();
        siteRules.Clear(); siteRules.AddRange(snapshot.RouteRules); RefreshRules();
        programRules.Clear(); programRules.AddRange(snapshot.ProgramRules); RefreshRules();
        measurements.Clear(); foreach (var measurement in snapshot.Measurements) measurements[measurement.Fingerprint] = measurement;
        favorites.Clear(); foreach (var favorite in snapshot.Favorites) favorites.Add(favorite);
        // Профиль из файла проверяется так же, как присланный сервисом.
        providerRouting = snapshot.ProviderRouting?.Normalized(); useProviderRouting = snapshot.UseProviderRouting;
        Routes.RussiaDirect.IsChecked = snapshot.RussiaDirect;
        autoSelect = snapshot.AutoSelect;
        Diagnostics.UpdateAuto.IsChecked = snapshot.UpdateCheck;
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
