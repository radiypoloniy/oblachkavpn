namespace Oblachka;

public partial class MainWindow
{
    private void CheckSavedSession(List<string> checks)
    {
        serviceProfiles = Array.Empty<Profile>(); externalProfiles = Array.Empty<Profile>(); profile = null;
        LoadServerPreview();
        ProfilePicker.SelectedIndex = 3;
        subscriptionSources = new[] { "https://fixture.invalid/sub/synthetic-token" }; preferredSource = 0;
        Settings.StayInTray.IsChecked = true; AutoRecover.IsChecked = true; Settings.Hotkey.IsChecked = false;
        var snapshot = CaptureState();
        var folder = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        var store = new ClientStateStore(Path.Combine(folder, "ui-session.bin"));
        store.Save(snapshot);
        LoadPreviewProfiles();
        Settings.StayInTray.IsChecked = false; AutoRecover.IsChecked = false; Settings.Hotkey.IsChecked = true;
        ApplySnapshot(store.Load()!);
        if (ProfilePicker.Items.Count != 24 || profile?.DisplayName != "Рабочий сервер" ||
            profile is null || Profiles.Fingerprint(profile) != snapshot.SelectedFingerprint ||
            !subscriptionSources.SequenceEqual(snapshot.SubscriptionSources) || !RefreshButton.IsEnabled ||
            Settings.StayInTray.IsChecked != true || AutoRecover.IsChecked != true || Settings.Hotkey.IsChecked != false || engine.ProcessId is not null)
            throw new InvalidOperationException("Saved session lost selection, settings or sources, or started a tunnel without consent");
        Settings.StayInTray.IsChecked = false; Settings.Hotkey.IsChecked = true;
        checks.Add("Explicit encrypted session restoration preserves names, selection, subscription and settings while leaving the tunnel stopped");
    }
}
