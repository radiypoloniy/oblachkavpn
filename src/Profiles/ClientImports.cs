namespace Oblachka;

public sealed record ClientImportChange(ProfileGroup Group, ImportResult Result, bool NewSubscription);

// Изменение модели атомарно в потоке владельца. Представление только перерисовывает результат.
public sealed partial class ClientImports : IDisposable
{
    private readonly ClientRuntime owner;
    private readonly Func<IReadOnlyList<string>, int, CancellationToken, Task<MirrorResult>> fetch;
    private readonly Func<DateTimeOffset> now;
    private CancellationTokenSource? active;
    private bool disposed;
    public int Revision { get; internal set; }
    public (ProfileGroup Group, ImportResult Result)? Pending { get; internal set; }
    public string Message { get; private set; } = "";
    public Task? Loading { get; private set; }
    public bool IsActive => active is not null;
    public event Action? Changed;
    public event Action<ClientImportChange>? Applied;
    internal ClientImports(ClientRuntime owner,
        Func<IReadOnlyList<string>, int, CancellationToken, Task<MirrorResult>>? fetch = null, Func<DateTimeOffset>? now = null)
    {
        this.owner = owner; this.fetch = fetch ?? SubscriptionMirrors.FetchAsync; this.now = now ?? (() => DateTimeOffset.UtcNow);
        owner.Connections.Changed += TryApplyPending;
    }
    public void ImportText(string input)
    {
        var result = ShareLinks.IsLink(input.Trim()) && !input.Trim().Contains('\n')
            ? new ImportResult([Profiles.Parse(input)], 0, 0) : Subscriptions.Parse(input);
        Apply(ProfileGroup.Manual, result.WithSource(ProfileSource.External));
    }
    public void Replace(ProfileGroup group)
    {
        var groups = owner.Profiles.Groups; var index = groups.FindIndex(g => g.Id == group.Id);
        if (index < 0) return;
        var old = SubscriptionState.For(groups[index], now()); var next = SubscriptionState.For(group, now());
        if (old.WarningKey != next.WarningKey || next.RemainingBytes > old.RemainingBytes) group = group with { DismissedWarning = null };
        groups[index] = group; Revision++; SyncProvider();
    }
    public void SetGroup(ProfileGroup group, IReadOnlyList<Profile> profiles)
    {
        var data = owner.Profiles; var exists = data.Groups.Any(g => g.Id == group.Id);
        var tagged = profiles.Select(p => p with { Source = group.Source, Group = group.Id }).ToArray();
        if (!exists && data.Groups.Count >= ProfileGroup.MaxGroups) throw new UserError($"Не больше {ProfileGroup.MaxGroups} подписок: уберите ненужную в «Настройки → Подписки и ключ».");
        if (data.GroupProfiles.Where(p => p.Key != group.Id).Sum(p => p.Value.Count) + tagged.Length > ProfileGroup.MaxTotalProfiles)
            throw new UserError($"Вместе подключений больше {ProfileGroup.MaxTotalProfiles}: уберите ненужную подписку.");
        owner.Workspace.RemovedSubscriptions.Remove(group.Id);
        if (exists) Replace(group); else data.Groups.Add(group);
        data.GroupProfiles[group.Id] = tagged; Revision++;
        var ordered = ProfileGroups.Ordered(data.Groups); data.Groups.Clear(); data.Groups.AddRange(ordered); SyncProvider(); Wake();
    }
    public void Apply(ProfileGroup group, ImportResult result, bool preserveSelection = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var data = owner.Profiles;
        var isNew = group.Source == ProfileSource.External && !group.IsManual && data.Groups.All(g => g.Id != group.Id);
        SetGroup(group, group.IsManual ? ProfileGroups.Append(ProfilesOf(group.Id), result.Profiles) : result.Profiles);
        if (result.Metadata?.Routing is { } routing)
        {
            if (owner.TunnelPolicy.ProviderRouting is { } old && old.Name != routing.Name) owner.TunnelPolicy.UseProviderRouting = null;
            owner.TunnelPolicy.ProviderRouting = routing;
        }
        Rebuild(preserveSelection);
        if (group.IsManual && !preserveSelection && result.Profiles.FirstOrDefault() is { } added &&
            data.Imported.FirstOrDefault(p => p.Group == group.Id && p.Name == added.Name && Profiles.Fingerprint(p) == Profiles.Fingerprint(added)) is { } selected)
            data.Selected = selected;
        SyncPicker();
        Message = $"Добавлено подключений: {result.Profiles.Count}.";
        if (result.Unsupported + result.Invalid > 0) Message += $" Пропущено: неподдерживаемых {result.Unsupported}, некорректных {result.Invalid}.";
        if (result.Reasons.Count > 0) Message += " Причины: " + string.Join(" | ", result.Reasons);
        owner.Persistence.Queue(); Applied?.Invoke(new(group, result, isNew)); Changed?.Invoke();
    }
    internal IReadOnlyList<Profile> ProfilesOf(string id) => owner.Profiles.GroupProfiles.GetValueOrDefault(id) ?? [];
    private void SyncProvider() => DohResolver.Preferred = owner.Profiles.Groups.FirstOrDefault(g => g.Id == ProfileGroup.ServiceId)?.Metadata?.Provider?.Resolve;
    private void Rebuild(bool preserveSelection)
    {
        var data = owner.Profiles;
        var combined = data.Groups.SelectMany(g => ProfilesOf(g.Id)).Where(p => !owner.Workspace.HiddenServers.Contains(AutoPool.Key(p))).ToArray();
        var selection = ProfileSelection.Find(combined, preserveSelection ? data.Selected : null);
        data.Imported = combined; data.Selected = combined.Length == 0 ? null : combined[selection]; data.TrimMeasurements();
    }
    private void SyncPicker()
    { owner.Profiles.Picker.ItemsSource = owner.Profiles.Imported; owner.Profiles.Picker.SelectedItem = owner.Profiles.Selected; }
    public void Remove(string id)
    {
        var data = owner.Profiles;
        if (data.Groups.FirstOrDefault(g => g.Id == id) is { IsSubscription: true })
        {
            owner.Workspace.RemovedSubscriptions.Add(id);
            while (owner.Workspace.RemovedSubscriptions.Count > 32) owner.Workspace.RemovedSubscriptions.Remove(owner.Workspace.RemovedSubscriptions.First());
        }
        Invalidate(id);
        if (Pending?.Group.Id == id) Pending = null;
        var selected = data.Selected?.Group == id;
        data.Groups.RemoveAll(g => g.Id == id); data.GroupProfiles.Remove(id); schedules.Remove(id); Revision++;
        if (selected) data.Selected = null;
        Rebuild(!selected); SyncPicker(); SyncProvider(); owner.Persistence.Queue(); Wake();
    }
    public void TryApplyPending()
    {
        if (disposed || Pending is not { } pending || owner.Activity.Operation is not null || owner.Engine.State != ConnectionState.Idle) return;
        Pending = null;
        if (!owner.Workspace.RemovedSubscriptions.Contains(pending.Group.Id))
        {
            try { Apply(pending.Group, pending.Result, true); }
            catch (UserError error) { Message = error.Message; Changed?.Invoke(); }
        }
    }
    private void SetMessage(string text) { Message = text; if (!disposed) Changed?.Invoke(); }
    public void Dispose()
    {
        if (disposed) return; disposed = true; StopSchedule(); active?.Cancel();
        owner.Connections.Changed -= TryApplyPending;
        Applied = null; Changed = null; Pending = null;
    }
}
