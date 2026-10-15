namespace Oblachka;

// Несколько подписок: группы, вкладки, удаление и сохранение.
public partial class MainWindow
{
    private async Task CheckGroupsAsync(List<string> checks)
    {
        serviceProfiles = Array.Empty<Profile>(); externalProfiles = Array.Empty<Profile>(); profile = null;
        Profile Node(string name, int n) => Profiles.Parse($"vless://{n:x8}-1111-4111-8111-111111111111@{name.ToLowerInvariant()}.invalid:443?security=tls&type=tcp#{Uri.EscapeDataString(name)}");
        Task Load(string url, ImportResult result) => LoadSubscriptionAsync(SubscriptionMirrors.ParseSources(url), 0, (_, _, _) => Task.FromResult(new MirrorResult(result, 0, 1)));
        var main = new ImportResult(new[] { Node("Литва", 1), Node("Индия", 2) }, 0, 0) { Metadata = new SubscriptionMetadata(null, null, null, null) { Title = "Основная" } };
        await Load("https://main.invalid/subs/a", main);
        await Load("https://other.invalid/s/b", new ImportResult(new[] { Node("Швеция", 3) }, 0, 0));
        Import("vless://44444444-1111-4111-8111-111111111111@manual.invalid:443?security=tls&type=tcp#Мой");
        Import("vless://55555555-1111-4111-8111-111111111111@manual2.invalid:443?security=tls&type=tcp#Мой второй");
        Import("vless://44444444-1111-4111-8111-111111111111@manual.invalid:443?security=tls&type=tcp#Мой");
        if (groups.Count != 3 || groups[0].Name != "Основная" || groups[1].Name != "other.invalid" || !groups[2].IsManual ||
            ProfilesOf(ProfileGroup.ManualId).Count != 2 || importedProfiles.Count != 5 || profile?.Name != "Мой")
            throw new InvalidOperationException("Groups: " + string.Join(", ", groups.Select(g => g.Name + "=" + ProfilesOf(g.Id).Count)));
        // Та же подписка ещё раз — обновление её группы, не вторая вкладка.
        await Load("https://main.invalid/subs/a?fresh=1", new ImportResult(new[] { Node("Литва", 1) }, 0, 0));
        if (groups.Count != 3 || ProfilesOf(groups[0].Id).Count != 1 || groups[0].Name != "Основная")
            throw new InvalidOperationException("Same subscription created a second group");
        checks.Add("Several subscriptions live side by side; the same subscription updates its own group; manual links append");

        RefreshGroups();
        if (Servers.GroupTabs.Items.Count != 4 || Servers.GroupTabsScroll.Visibility != System.Windows.Visibility.Visible ||
            Settings.Subscription.GroupList.Items.Count != 3)
            throw new InvalidOperationException("Group tabs or settings list missing");
        Servers.GroupTabs.SelectedIndex = 2;
        if (ProfilePicker.Items.Count != 1 || ((Profile)ProfilePicker.Items[0]).Name != "Швеция")
            throw new InvalidOperationException("Group tab does not filter servers");
        Servers.GroupTabs.SelectedIndex = 0;
        if (ProfilePicker.Items.Count != 4) throw new InvalidOperationException("«Все» tab does not show every server");
        checks.Add("Server sheet shows a tab per subscription and filters by it");

        var snapshot = CaptureState();
        var folder = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        var store = new ClientStateStore(Path.Combine(folder, "groups.bin"));
        store.Save(snapshot);
        RemoveGroup(groups[1].Id);
        if (groups.Count != 2 || importedProfiles.Any(p => p.Name == "Швеция") || profile?.Name != "Мой")
            throw new InvalidOperationException("Removing a subscription broke the list or selection");
        ApplySnapshot(store.Load()!);
        if (groups.Count != 3 || groups[1].Sources.Single() != "https://other.invalid/s/b" || importedProfiles.Count != 4 || profile?.Name != "Мой")
            throw new InvalidOperationException("Groups did not survive saving");
        // Снимок 0.10: одна сторонняя подписка — её группа, а не «Вручную».
        var legacy = snapshot with { Groups = null, ExternalSources = new[] { "https://other.invalid/s/b" },
            Profiles = snapshot.Profiles.Where(p => p.Group == groups[1].Id).Select(p => p with { Group = null }).ToArray() };
        ApplySnapshot(legacy);
        if (groups.Count != 1 || groups[0].Id != ProfileGroup.IdFor(legacy.ExternalSources, ProfileSource.External) || importedProfiles.Count != 1)
            throw new InvalidOperationException("Legacy snapshot was not migrated into groups");
        checks.Add("Groups survive encrypted saving; removing one keeps the rest; 0.10 snapshots migrate");
        // Отдельные подключения другого клиента — во «Вручную», рядом с оставшейся подпиской.
        var foreign = new ForeignSubscription("Throne", "2 отдельных подключения", "") { Profiles = new[] {
            Profiles.Parse("vless://33333333-3333-4333-8333-333333333333@203.0.113.11:443?security=tls&sni=example.org#A") with { Name = "Дом" },
            Profiles.Parse("vless://44444444-4444-4444-8444-444444444444@203.0.113.12:443?security=tls&sni=example.org#B") with { Name = "Офис" } } };
        ImportForeignProfiles(foreign); ImportForeignProfiles(foreign);
        if (groups.Count != 2 || ProfilesOf(ProfileGroup.ManualId).Count != 2 || profile?.Name != "Дом" || importedProfiles.Count != 3)
            throw new InvalidOperationException("Standalone profiles from another client were not added to the manual group");
        checks.Add("Standalone profiles of another client land in «Вручную» once, beside subscriptions");
        serviceProfiles = Array.Empty<Profile>(); externalProfiles = Array.Empty<Profile>(); profile = null;
        LoadPreviewProfiles();
    }
}
