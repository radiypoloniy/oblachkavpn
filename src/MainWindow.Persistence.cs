using System.Windows;

namespace RoutePilot;

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
        importedProfiles.Select(p => new SavedProfile(p.Name, (System.Text.Json.Nodes.JsonObject)p.Outbound.DeepClone())).ToArray(),
        profile is null ? null : Profiles.Fingerprint(profile), subscriptionSources.ToArray(), preferredSource,
        Settings.StayInTray.IsChecked == true, AutoRecover.IsChecked == true)
        { AutoRefresh = Settings.AutoRefresh.IsChecked == true, Metadata = subscriptionMetadata, RouteRules = siteRules.ToArray(), ProgramRules = programRules.ToArray(),
            Measurements = measurements.Values.Where(m => importedProfiles.Any(p => Profiles.Fingerprint(p) == m.Fingerprint)).Take(200).ToArray() };

    private void SaveStateClick(object sender, RoutedEventArgs args)
    {
        if (stateStore is null || profile is null || operation is not null) return;
        if (pendingImport is not null) { DetailText.Text = "Сначала останови подключение, чтобы сохранить обновлённый список."; return; }
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
        subscriptionSources = snapshot.SubscriptionSources.ToArray(); preferredSource = snapshot.PreferredSource;
        Settings.StayInTray.IsChecked = snapshot.StayInTray; AutoRecover.IsChecked = snapshot.AutoRecover;
        Settings.AutoRefresh.IsChecked = snapshot.AutoRefresh;
        siteRules.Clear(); siteRules.AddRange(snapshot.RouteRules); RefreshRules();
        programRules.Clear(); programRules.AddRange(snapshot.ProgramRules); RefreshRules();
        measurements.Clear(); foreach (var measurement in snapshot.Measurements) measurements[measurement.Fingerprint] = measurement;
        ApplyImport(new ImportResult(restored, 0, 0) { Metadata = snapshot.Metadata });
        var selected = restored.FirstOrDefault(p => Profiles.Fingerprint(p) == snapshot.SelectedFingerprint);
        if (selected is not null) ProfilePicker.SelectedItem = selected;
    }
}
