namespace Oblachka;

public sealed partial class ClientImports
{
    private long revision;
    private string? loadingId;
    private void Invalidate(string id) { if (loadingId == id) { revision++; active?.Cancel(); } }
    private bool ForeignConnected => owner.Tunnels.ForeignSession && owner.Tunnel is { State: not (SystemTunnelState.Idle or SystemTunnelState.Failed) };
    public Task LoadAsync(IReadOnlyList<string> sources, int preferred) => LoadAsync(sources, preferred, null);
    internal Task LoadAsync(IReadOnlyList<string> sources, int preferred,
        Func<IReadOnlyList<string>, int, CancellationToken, Task<MirrorResult>>? loader)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (owner.Activity.Operation is not null || owner.Connections.IsActive || owner.Connections.IsStopping) return Task.CompletedTask;
        return Loading = LoadCoreAsync(sources, preferred, loader ?? fetch);
    }
    private async Task LoadCoreAsync(IReadOnlyList<string> sources, int preferred,
        Func<IReadOnlyList<string>, int, CancellationToken, Task<MirrorResult>> loader)
    {
        sources = SubscriptionMirrors.ParseSources(string.Join('\n', sources));
        var kind = ServiceKey.SourceOf(sources);
        if (ForeignConnected && kind == ProfileSource.Service)
        { SetMessage("Подписка сервиса обновится после отключения от бесплатного сервера."); return; }
        var previous = owner.Profiles.Groups.FirstOrDefault(g => g.Sources.SequenceEqual(sources)) ??
            owner.Profiles.Groups.FirstOrDefault(g => g.Id == ProfileGroup.IdFor(sources, kind));
        var id = previous?.Id ?? ProfileGroup.IdFor(sources, kind); var expected = revision; loadingId = id;
        var schedule = ScheduleFor(id);
        using var cancel = new CancellationTokenSource(); active = cancel;
        owner.Activity.ManualStop = false; owner.Activity.Operation = cancel; owner.Activity.EngineOperation = false;
        SetMessage("Загружаем подписку; при отказе проверим резервные адреса…");
        try
        {
            var result = await loader(sources, preferred, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (disposed || expected != revision) return;
            var metadata = result.Import.Metadata ?? previous?.Metadata;
            var current = result.Sources ?? sources;
            var group = new ProfileGroup(id, ProfileGroup.NameFor(current, metadata, kind), kind, current.ToArray())
            { PreferredSource = result.SourceIndex, Metadata = metadata, Updated = now(),
                DismissedWarning = previous?.DismissedWarning, ExpiryNotice = previous?.ExpiryNotice };
            var imported = result.Import.WithSource(kind);
            if (owner.Engine.State is ConnectionState.Running or ConnectionState.Verified && owner.Profiles.Running is { } running &&
                running.Group == id && ProfileSelection.Locate(imported.Profiles, running) < 0)
            {
                // Валидируем лимиты до изменения метаданных и отложенного списка.
                if (owner.Profiles.GroupProfiles.Where(p => p.Key != id).Sum(p => p.Value.Count) + imported.Profiles.Count > ProfileGroup.MaxTotalProfiles)
                    throw new UserError("Вместе подключений больше допустимого: уберите ненужную подписку.");
                Replace(group); Pending = (group, imported); owner.Persistence.Queue();
                SetMessage($"Получено подключений: {imported.Profiles.Count}; адрес №{result.SourceIndex + 1}. Сервера, к которому вы подключены, в подписке больше нет: новый список применится после отключения.");
            }
            else
            {
                Apply(group, imported, true);
                if (Pending?.Group.Id == id) Pending = null;
                SetMessage(Message + $" Загрузка: адрес №{result.SourceIndex + 1}, попыток {result.Attempts}.");
            }
            schedule.Complete(now(), true, result.Import.Metadata?.UpdateInterval);
            if (result.Moved) SetMessage(Message + " Подписка переехала на новый адрес, прежний оставлен запасным.");
            owner.Persistence.Queue();
        }
        catch (OperationCanceledException) { schedule.Complete(now(), false); SetMessage("Загрузка отменена. Предыдущий список сохранён."); }
        catch (SubscriptionAccessDenied error) { schedule.AccessDenied(); SetMessage(error.Message); }
        catch (UserError error) { schedule.Complete(now(), false); SetMessage(error.Message); }
        catch (IOException) { schedule.Complete(now(), false); SetMessage("Не удалось загрузить подписку. Предыдущий список сохранён."); }
        finally
        {
            if (ReferenceEquals(owner.Activity.Operation, cancel)) owner.Activity.Operation = null;
            active = null; loadingId = null;
            if (!disposed) { TryApplyPending(); Changed?.Invoke(); Wake(); }
        }
    }
    public async Task RefreshAllAsync()
    {
        foreach (var group in owner.Profiles.Groups.Where(g => g.IsSubscription).ToArray())
        {
            if (disposed || owner.Activity.Operation is not null) return;
            if (owner.Profiles.Groups.All(g => g.Id != group.Id)) continue;
            await LoadAsync(group.Sources, group.PreferredSource);
            if (owner.Activity.ManualStop) return;
        }
    }
}
