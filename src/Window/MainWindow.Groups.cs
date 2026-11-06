namespace Oblachka;

// Подписки и «Вручную» как группы (ProfileGroup). Прежние поля — serviceProfiles, subscriptionSources,
// subscriptionMetadata — остались свойствами поверх групп: остальной код и проверки читают их как раньше.
public partial class MainWindow
{
    private List<ProfileGroup> groups => runtime.Profiles.Groups;
    private Dictionary<string, IReadOnlyList<Profile>> groupProfiles => runtime.Profiles.GroupProfiles;

    private ProfileGroup? ServiceGroup => groups.FirstOrDefault(g => g.Id == ProfileGroup.ServiceId);
    private IReadOnlyList<Profile> ProfilesOf(string id) => groupProfiles.TryGetValue(id, out var list) ? list : Array.Empty<Profile>();
    private ProfileGroup? GroupOf(Profile? item) => item is null ? null : groups.FirstOrDefault(g => g.Id == item.Group);

    private ProfileGroup? CurrentGroup => ProfileGroups.Active(groups, profile, runningProfile, ConnectionActive);
    private int groupRevision { get => runtime.Imports.Revision; set => runtime.Imports.Revision = value; }
    private ProfileGroup? stateGroup;
    private long stateTime;
    private SubscriptionState? subscriptionState;
    private SubscriptionState? CurrentSubscription
    {
        get
        {
            var group = CurrentGroup; var now = DateTimeOffset.UtcNow;
            var time = SubscriptionState.RefreshKey(group, now);
            if (!ReferenceEquals(group, stateGroup) || time != stateTime)
            {
                stateGroup = group; stateTime = time;
                subscriptionState = group is null ? null : SubscriptionState.For(group, now);
            }
            return subscriptionState;
        }
    }

    private IReadOnlyList<Profile> serviceProfiles
    {
        get => ProfilesOf(ProfileGroup.ServiceId);
        set
        {
            if (value.Count == 0) { groups.RemoveAll(g => g.Id == ProfileGroup.ServiceId); groupProfiles.Remove(ProfileGroup.ServiceId); groupRevision++; }
            else SetGroup(ServiceGroup ?? new(ProfileGroup.ServiceId, ServiceBrand.Name, ProfileSource.Service, Array.Empty<string>()), value);
        }
    }

    private IReadOnlyList<Profile> externalProfiles
    {
        get => groups.Where(g => g.Source == ProfileSource.External).SelectMany(g => ProfilesOf(g.Id)).ToArray();
        set
        {
            foreach (var group in groups.Where(g => g.Source == ProfileSource.External).ToArray()) { groups.Remove(group); groupProfiles.Remove(group.Id); groupRevision++; }
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
        set { if (CurrentGroup is { } group) Replace(group with { Metadata = value }); }
    }

    private void UpdateService(Func<ProfileGroup, ProfileGroup> change)
    {
        var group = ServiceGroup ?? new(ProfileGroup.ServiceId, ServiceBrand.Name, ProfileSource.Service, Array.Empty<string>());
        if (ServiceGroup is null) { groups.Insert(0, group); groupProfiles[group.Id] = Array.Empty<Profile>(); }
        Replace(change(group));
    }

    private void Replace(ProfileGroup group)
    {
        runtime.Imports.Replace(group);
    }

    // Новая или обновлённая группа целиком. Узлы получают её источник и идентификатор.
    // Убранные подписки (идентификаторы групп): ссылка oblachka:// на такую спрашивает отдельно.
    private HashSet<string> removedSubscriptions => runtime.Workspace.RemovedSubscriptions;
    private void TrimRemoved() { while (removedSubscriptions.Count > 32) removedSubscriptions.Remove(removedSubscriptions.First()); }

    private void SetGroup(ProfileGroup group, IReadOnlyList<Profile> profiles)
    {
        runtime.Imports.SetGroup(group, profiles);
    }

    private void RemoveGroup(string id)
    {
        if (operation is not null) return;
        runtime.Imports.Remove(id);
        ApplyProfiles(true);
        RefreshGroups();
        UpdateControls();
        QueueStateSave(); FlushStateSave();
    }

    // Подпись узла в списке: у сторонних — имя его подписки.
    private string? GroupNote(Profile item) => item.IsService ? null : GroupOf(item) is { IsManual: false } group ? group.Name : "вручную";
}
