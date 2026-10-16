namespace Oblachka;

// Подписки и «Вручную» как группы (ProfileGroup). Прежние поля — serviceProfiles, subscriptionSources,
// subscriptionMetadata — остались свойствами поверх групп: остальной код и проверки читают их как раньше.
public partial class MainWindow
{
    private readonly List<ProfileGroup> groups = new();
    private readonly Dictionary<string, IReadOnlyList<Profile>> groupProfiles = new();

    private ProfileGroup? ServiceGroup => groups.FirstOrDefault(g => g.Id == ProfileGroup.ServiceId);
    private IReadOnlyList<Profile> ProfilesOf(string id) => groupProfiles.TryGetValue(id, out var list) ? list : Array.Empty<Profile>();
    private ProfileGroup? GroupOf(Profile? item) => item is null ? null : groups.FirstOrDefault(g => g.Id == item.Group);

    // Срок и трафик на экране — подписки выбранного узла; без неё — сервиса или первой подписки.
    private ProfileGroup? CurrentGroup => GroupOf(profile) is { IsSubscription: true } selected ? selected
        : ServiceGroup ?? groups.FirstOrDefault(g => g.IsSubscription);

    private IReadOnlyList<Profile> serviceProfiles
    {
        get => ProfilesOf(ProfileGroup.ServiceId);
        set
        {
            if (value.Count == 0) { groups.RemoveAll(g => g.Id == ProfileGroup.ServiceId); groupProfiles.Remove(ProfileGroup.ServiceId); }
            else SetGroup(ServiceGroup ?? new(ProfileGroup.ServiceId, ServiceBrand.Name, ProfileSource.Service, Array.Empty<string>()), value);
        }
    }

    private IReadOnlyList<Profile> externalProfiles
    {
        get => groups.Where(g => g.Source == ProfileSource.External).SelectMany(g => ProfilesOf(g.Id)).ToArray();
        set
        {
            foreach (var group in groups.Where(g => g.Source == ProfileSource.External).ToArray()) { groups.Remove(group); groupProfiles.Remove(group.Id); }
            if (value.Count > 0) SetGroup(ProfileGroup.Manual, value);
        }
    }

    private IReadOnlyList<string> subscriptionSources
    {
        get => ServiceGroup?.Sources ?? Array.Empty<string>();
        set => UpdateService(g => g with { Sources = value.ToArray(), PreferredSource = 0 });
    }

    private int preferredSource
    {
        get => ServiceGroup?.PreferredSource ?? 0;
        set => UpdateService(g => g with { PreferredSource = value });
    }

    // Адреса первой сторонней подписки — для прежних мест, где она была одна.
    private IReadOnlyList<string> externalSources => groups.FirstOrDefault(g => g.Source == ProfileSource.External && g.IsSubscription)?.Sources ?? Array.Empty<string>();

    private SubscriptionMetadata? subscriptionMetadata
    {
        get => CurrentGroup?.Metadata;
        set { if ((ServiceGroup ?? CurrentGroup) is { } group) Replace(group with { Metadata = value }); }
    }

    private void UpdateService(Func<ProfileGroup, ProfileGroup> change)
    {
        var group = ServiceGroup ?? new(ProfileGroup.ServiceId, ServiceBrand.Name, ProfileSource.Service, Array.Empty<string>());
        if (ServiceGroup is null) { groups.Insert(0, group); groupProfiles[group.Id] = Array.Empty<Profile>(); }
        Replace(change(group));
    }

    private void Replace(ProfileGroup group)
    {
        var index = groups.FindIndex(g => g.Id == group.Id);
        if (index >= 0) groups[index] = group;
    }

    // Новая или обновлённая группа целиком. Узлы получают её источник и идентификатор.
    private void SetGroup(ProfileGroup group, IReadOnlyList<Profile> profiles)
    {
        var tagged = profiles.Select(p => p with { Source = group.Source, Group = group.Id }).ToArray();
        var exists = groups.Any(g => g.Id == group.Id);
        if (!exists && groups.Count >= ProfileGroup.MaxGroups) throw new UserError($"Не больше {ProfileGroup.MaxGroups} подписок: уберите ненужную в «Настройки → Подписки и ключ».");
        var total = groupProfiles.Where(p => p.Key != group.Id).Sum(p => p.Value.Count) + tagged.Length;
        if (total > ProfileGroup.MaxTotalProfiles) throw new UserError($"Вместе подключений больше {ProfileGroup.MaxTotalProfiles}: уберите ненужную подписку.");
        if (exists) Replace(group); else groups.Add(group);
        groupProfiles[group.Id] = tagged;
        var ordered = ProfileGroups.Ordered(groups);
        groups.Clear(); groups.AddRange(ordered);
    }

    private void RemoveGroup(string id)
    {
        if (operation is not null) return;
        var removingSelected = profile?.Group == id;
        groups.RemoveAll(g => g.Id == id); groupProfiles.Remove(id);
        if (removingSelected) profile = null;
        ApplyProfiles(!removingSelected);
        RefreshGroups();
        UpdateControls();
        QueueStateSave();
    }

    // Обновить все подписки: сервис первым, затем сторонние. «Вручную» не обновляется.
    private async Task RefreshAllAsync()
    {
        foreach (var group in groups.Where(g => g.IsSubscription).ToArray())
        {
            if (closed || operation is not null) return;
            await LoadSubscriptionAsync(group.Sources, group.PreferredSource);
        }
    }

    // Подпись узла в списке: у сторонних — имя его подписки.
    private string? GroupNote(Profile item) => item.IsService ? null : GroupOf(item) is { IsManual: false } group ? group.Name : "вручную";
}
