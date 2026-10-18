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

        // Сервер подписки скрывается (подписка прислала бы его снова), свой — удаляется.
        RemoveServer(importedProfiles.First(p => p.Name == "Швеция"), confirm: false);
        RenderSheet();
        if (importedProfiles.Count != 2 || HiddenIn(groups[0].Id) != 1 ||
            Servers.SheetList.Items.OfType<SheetHeader>().FirstOrDefault(h => h.GroupId == groups[0].Id)?.Hidden != 1)
            throw new InvalidOperationException("Hiding a subscription server failed or its header lost «Вернуть скрытые»");
        store.Save(CaptureState()); hiddenServers.Clear(); ApplySnapshot(store.Load()!);
        if (importedProfiles.Any(p => p.Name == "Швеция") || HiddenIn(groups[0].Id) != 1)
            throw new InvalidOperationException("Hidden server came back after saving");
        UnhideServers(groups[0].Id);
        if (!importedProfiles.Any(p => p.Name == "Швеция")) throw new InvalidOperationException("«Вернуть скрытые» did not return the server");
        RemoveServer(importedProfiles.First(p => p.Name == "Офис"), confirm: false);
        if (ProfilesOf(ProfileGroup.ManualId).Count != 1 || importedProfiles.Count != 2)
            throw new InvalidOperationException("Deleting a manual server failed");
        checks.Add("A subscription server hides (and survives saving hidden, returns on demand); a manual server deletes");
        // Убранная подписка запоминается: ссылка со страницы подписки спросит «Вернуть?», а не «Добавить?».
        var swedenGroup = groups.First(g => g.IsSubscription).Id;
        RemoveGroup(swedenGroup);
        if (!removedSubscriptions.Contains(swedenGroup)) throw new InvalidOperationException("Removed subscription was not remembered");
        store.Save(CaptureState()); removedSubscriptions.Clear(); ApplySnapshot(store.Load()!);
        if (!removedSubscriptions.Contains(swedenGroup)) throw new InvalidOperationException("Removed subscription was forgotten after saving");
        await Load("https://other.invalid/s/b", new ImportResult(new[] { Node("Швеция", 3) }, 0, 0));
        if (removedSubscriptions.Contains(swedenGroup)) throw new InvalidOperationException("Re-added subscription still counted as removed");
        checks.Add("A removed subscription is remembered across saving and forgotten once added back");
        // Убрать — значит насовсем: отложенное до отключения обновление её не вернёт, а пустой список сохраняется.
        var pendingGroup = groups.First(g => g.IsSubscription);
        pendingImport = (pendingGroup, new ImportResult(new[] { Node("Швеция", 3) }, 0, 0));
        RemoveGroup(pendingGroup.Id);
        if (pendingImport is not null || groups.Any(g => g.Id == pendingGroup.Id))
            throw new InvalidOperationException("A pending refresh survived removing its subscription");
        UpdateControls();
        if (groups.Any(g => g.Id == pendingGroup.Id)) throw new InvalidOperationException("A pending refresh brought a removed subscription back");
        foreach (var id in groups.Select(g => g.Id).ToArray()) RemoveGroup(id);
        store.Save(CaptureState());
        ApplySnapshot(store.Load()!);
        if (groups.Count != 0 || importedProfiles.Count != 0) throw new InvalidOperationException("Removing every subscription did not survive saving");
        checks.Add("Removing a subscription is final: a pending refresh cannot bring it back and an empty list is saved");
        hiddenServers.Clear();
        serviceProfiles = Array.Empty<Profile>(); externalProfiles = Array.Empty<Profile>(); profile = null;
        LoadPreviewProfiles();
    }
}
