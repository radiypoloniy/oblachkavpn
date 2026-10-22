using System.Text.Json.Serialization;

namespace Oblachka;

public enum NetworkSessionState { Prepare, Applied, Restoring, Released, RecoveryRequired }
public enum NetworkChangeState { Planned, Applied, Restored, Conflict }
// Порядок значений задаёт порядок применения: сессия применяет изменения по списку,
// а снимает в обратном. Защита стоит первой, потому что захват трафика показал,
// что утечки лежат на краях окна — до поднятия туннеля и после его снятия.
// WfpProtection — фильтры Windows, KillSwitch — прежняя блокировка брандмауэром.
// В журнал значения пишутся строками, поэтому порядок можно менять.
public enum NetworkResourceKind { WfpProtection, KillSwitch, PeerRoute, TunnelInterface, DefaultRoute, Dns }
public enum NetworkWriteResult { Changed, Unchanged, Conflict }

// Only a trusted backend prepares these values. They are not an IPC command or core configuration.
public sealed record NetworkChange(
    [property: JsonRequired] NetworkResourceKind Kind,
    [property: JsonRequired] string ResourceId,
    [property: JsonRequired] string? Before,
    [property: JsonRequired] string After,
    [property: JsonRequired] NetworkChangeState State = NetworkChangeState.Planned);

public sealed record NetworkSessionRecord(
    [property: JsonRequired] int Version,
    [property: JsonRequired] Guid LeaseId,
    [property: JsonRequired] NetworkSessionState State,
    [property: JsonRequired] NetworkChange[] Changes);

public interface INetworkSessionJournal
{
    object SyncRoot { get; }
    NetworkSessionRecord? Load();
    void Save(NetworkSessionRecord record);
}

public interface INetworkSessionBackend
{
    // Prepare must have no side effects. IDs/values must be derived locally, never accepted from IPC.
    IReadOnlyList<NetworkChange> Prepare(ValidatedNetworkLease lease);
    // Apply compares Before; AlreadyApplied requires matching After AND lease ownership.
    NetworkWriteResult Apply(Guid leaseId, NetworkChange change);
    // Restore compares After AND ownership; unrelated changes return Conflict without writing.
    // Original Before is Unchanged. Expected OS failures become IOException/UserError.
    NetworkWriteResult Restore(Guid leaseId, NetworkChange change);
    // Цело ли применённое изменение: его могли снять без нас (NetworkSession.Guard).
    bool Intact(NetworkChange change) => true;
}

public static class NetworkSessionValidation
{
    public static void Validate(NetworkSessionRecord record)
    {
        if (record.Version != 1 || record.LeaseId == Guid.Empty || !Enum.IsDefined(record.State) ||
            record.Changes is null || record.Changes.Length is < 1 or > 32)
            throw new UserError("Неподдерживаемый журнал сетевой сессии.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var lastKind = NetworkResourceKind.WfpProtection;
        foreach (var change in record.Changes)
        {
            if (change is null || !Enum.IsDefined(change.Kind) || !Enum.IsDefined(change.State) ||
                !ValidText(change.ResourceId, 128) || !ValidText(change.After, 512) ||
                change.Before is not null && !ValidText(change.Before, 512) || change.Before == change.After ||
                !ids.Add(change.ResourceId) || change.Kind < lastKind)
                throw new UserError("Некорректные изменения в журнале сетевой сессии.");
            lastKind = change.Kind;
        }
        if (record.State == NetworkSessionState.Applied && record.Changes.Any(c => c.State != NetworkChangeState.Applied) ||
            record.State == NetworkSessionState.Released && record.Changes.Any(c => c.State != NetworkChangeState.Restored))
            throw new UserError("Состояние журнала не соответствует изменениям.");
    }

    private static bool ValidText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}
