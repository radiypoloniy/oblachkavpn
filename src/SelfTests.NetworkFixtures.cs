namespace RoutePilot;

public static partial class SelfTests
{
    private sealed class SimulatedNetworkCrash : Exception;

    private sealed class MemoryNetworkJournal : INetworkSessionJournal
    {
        public object SyncRoot { get; } = new();
        public NetworkSessionRecord? Record;
        public int Writes;
        public Action<int, bool>? WriteHook;
        public NetworkSessionRecord? Load() => Record is null ? null : Copy(Record);
        public void Save(NetworkSessionRecord record)
        {
            NetworkSessionValidation.Validate(record);
            var number = ++Writes;
            WriteHook?.Invoke(number, false);
            Record = Copy(record);
            WriteHook?.Invoke(number, true);
        }
        private static NetworkSessionRecord Copy(NetworkSessionRecord value) => value with { Changes = value.Changes.ToArray() };
    }

    // In-memory network only: no Windows routes, DNS, interfaces, processes or external requests.
    private sealed class FakeNetworkBackend : INetworkSessionBackend
    {
        public readonly Dictionary<string, string?> Values = new() { ["dns"] = "original-dns" };
        public readonly Dictionary<string, Guid> Owners = new();
        public readonly List<string> Operations = new();
        public Action<int, bool>? ApplyHook;
        public Action<int, bool>? RestoreHook;
        public Action? BeforeApply;
        public int ApplyCalls;
        public int RestoreCalls;
        public IReadOnlyList<NetworkChange> Prepare(ValidatedNetworkLease lease) => new[]
        {
            new NetworkChange(NetworkResourceKind.PeerRoute, "peer", null, "peer-uplink"),
            new NetworkChange(NetworkResourceKind.TunnelInterface, "tun", null, "own-interface"),
            new NetworkChange(NetworkResourceKind.DefaultRoute, "route", null, "own-default"),
            new NetworkChange(NetworkResourceKind.Dns, "dns", Values.GetValueOrDefault("dns"), "own-dns")
        };

        public NetworkWriteResult Apply(Guid leaseId, NetworkChange change)
        {
            BeforeApply?.Invoke();
            var number = ++ApplyCalls;
            ApplyHook?.Invoke(number, false);
            var current = Values.GetValueOrDefault(change.ResourceId);
            if (current == change.After && Owners.GetValueOrDefault(change.ResourceId) == leaseId) return NetworkWriteResult.Unchanged;
            if (current != change.Before || Owners.ContainsKey(change.ResourceId)) return NetworkWriteResult.Conflict;
            Values[change.ResourceId] = change.After;
            Owners[change.ResourceId] = leaseId;
            Operations.Add("apply:" + change.ResourceId);
            ApplyHook?.Invoke(number, true);
            return NetworkWriteResult.Changed;
        }

        public NetworkWriteResult Restore(Guid leaseId, NetworkChange change)
        {
            var number = ++RestoreCalls;
            RestoreHook?.Invoke(number, false);
            var current = Values.GetValueOrDefault(change.ResourceId);
            if (current == change.Before && !Owners.ContainsKey(change.ResourceId)) return NetworkWriteResult.Unchanged;
            if (current != change.After || Owners.GetValueOrDefault(change.ResourceId) != leaseId) return NetworkWriteResult.Conflict;
            Values[change.ResourceId] = change.Before;
            Owners.Remove(change.ResourceId);
            Operations.Add("restore:" + change.ResourceId);
            RestoreHook?.Invoke(number, true);
            return NetworkWriteResult.Changed;
        }

        public bool Original => Owners.Count == 0 && Values.GetValueOrDefault("dns") == "original-dns" &&
            new[] { "peer", "tun", "route" }.All(id => Values.GetValueOrDefault(id) is null);
    }

    private static NetworkLeaseRequest NetworkAcquire() => new(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, new[] { "192.0.2.1" });
    private static void ExpectNetworkCrash(Action action)
    {
        try { action(); } catch (SimulatedNetworkCrash) { return; }
        throw new InvalidOperationException("Crash point was not reached");
    }
}
