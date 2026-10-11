namespace RoutePilot;

// Serializes transactions; the file journal separately prevents a second process owning the same path.
// No real backend is wired into the application at this stage.
public sealed class NetworkSession(INetworkSessionJournal journal, INetworkSessionBackend backend)
{
    private readonly object gate = journal.SyncRoot;

    public NetworkSessionState? State { get { lock (gate) return journal.Load()?.State; } }

    public NetworkSessionRecord Acquire(NetworkLeaseRequest request, CancellationToken token = default)
    {
        var lease = NetworkHelperContracts.Validate(request);
        if (lease.Action != NetworkLeaseAction.Acquire) throw new UserError("Для запуска нужна команда Acquire.");
        lock (gate)
        {
            var existing = journal.Load();
            if (existing is { State: not NetworkSessionState.Released })
                throw new UserError("Сначала восстанови предыдущую сетевую сессию.");
            token.ThrowIfCancellationRequested();
            var changes = backend.Prepare(lease).ToArray();
            var record = new NetworkSessionRecord(1, lease.LeaseId, NetworkSessionState.Prepare, changes);
            NetworkSessionValidation.Validate(record);
            if (changes.Any(c => c.State != NetworkChangeState.Planned)) throw new UserError("Новый план уже содержит выполненные изменения.");
            journal.Save(record); // Intent is durable BEFORE the first side effect.
            try
            {
                for (var i = 0; i < changes.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var result = backend.Apply(lease.LeaseId, changes[i]);
                    if (!Enum.IsDefined(result)) throw new UserError("Неизвестный результат сетевого изменения.");
                    if (result == NetworkWriteResult.Conflict) throw new UserError("Сетевой параметр изменён другим владельцем.");
                    changes[i] = changes[i] with { State = NetworkChangeState.Applied };
                    journal.Save(record with { Changes = changes.ToArray() });
                }
                token.ThrowIfCancellationRequested();
                record = record with { State = NetworkSessionState.Applied, Changes = changes.ToArray() };
                journal.Save(record);
                return record;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserError or OperationCanceledException)
            {
                // Reload durable intent: a write may fail AFTER the backend applied the change.
                // Cleanup is independent of the caller's cancellation token.
                RecoverCore();
                throw;
            }
        }
    }

    public NetworkSessionRecord Release(Guid leaseId)
    {
        lock (gate)
        {
            var record = journal.Load() ?? throw new UserError("Сетевая сессия не найдена.");
            if (record.LeaseId != leaseId) throw new UserError("Идентификатор сетевой сессии не совпал.");
            return RecoverCore()!;
        }
    }

    // For the helper's own startup/owner-loss path, never an unauthenticated IPC request.
    public NetworkSessionRecord? Recover()
    {
        lock (gate) return RecoverCore();
    }

    private NetworkSessionRecord? RecoverCore()
    {
        var record = journal.Load();
        if (record is null || record.State == NetworkSessionState.Released) return record;
        record = record with { State = NetworkSessionState.Restoring, Changes = record.Changes.ToArray() };
        journal.Save(record); // If this fails, touch NO further resources.
        for (var i = record.Changes.Length - 1; i >= 0; i--)
        {
            var change = record.Changes[i];
            if (change.State == NetworkChangeState.Restored) continue;
            NetworkWriteResult result;
            try { result = backend.Restore(record.LeaseId, change); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserError)
            {
                record = record with { State = NetworkSessionState.RecoveryRequired };
                journal.Save(record);
                return record;
            }
            if (!Enum.IsDefined(result)) throw new UserError("Неизвестный результат сетевого восстановления.");
            if (result == NetworkWriteResult.Conflict)
            {
                record.Changes[i] = change with { State = NetworkChangeState.Conflict };
                record = record with { State = NetworkSessionState.RecoveryRequired };
                journal.Save(record);
                return record; // Do not remove dependencies underneath an unresolved change.
            }
            record.Changes[i] = change with { State = NetworkChangeState.Restored };
            journal.Save(record);
        }
        record = record with { State = NetworkSessionState.Released };
        journal.Save(record);
        return record;
    }
}
