namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ArchitectureChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var id = Guid.NewGuid();
        await check("Network helper accepts only bounded exact-IP leases, without credentials or broad routes", () => Sync(() =>
        {
            var parsed = NetworkHelperContracts.Parse($"{{\"Version\":1,\"LeaseId\":\"{id}\",\"Action\":\"Acquire\",\"LoopbackPort\":10808,\"PeerAddresses\":[\"192.0.2.1\",\"2001:db8::1\"]}}");
            Assert(parsed.Peers.Count == 2 && parsed.LeaseId == id);
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, id, NetworkLeaseAction.Acquire, 10808, new[] { "0.0.0.0/0" })));
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, id, NetworkLeaseAction.Acquire, 10808, new[] { "server.example" })));
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, id, NetworkLeaseAction.Acquire, 10808, new[] { "127.0.0.1" })));
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, id, NetworkLeaseAction.Acquire, 10808, new[] { "224.1.1.1" })));
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, id, NetworkLeaseAction.Acquire, 10808, new[] { "1" })));
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, id, NetworkLeaseAction.Acquire, 10808, new[] { "169.254.1.1" })));
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, id, NetworkLeaseAction.Acquire, 10808, new[] { "192.0.2.1", "::ffff:192.0.2.1" })));
        }));
        await check("Helper rejects unknown commands, secrets, fields, versions and overlong payloads", () => Sync(() =>
        {
            ExpectUserError(() => NetworkHelperContracts.Parse($"{{\"Version\":1,\"LeaseId\":\"{id}\",\"Action\":\"Acquire\",\"LoopbackPort\":10808,\"PeerAddresses\":[\"192.0.2.1\"],\"Outbound\":{{}}}}"));
            ExpectUserError(() => NetworkHelperContracts.Parse(new string('x', 8193)));
            ExpectUserError(() => NetworkHelperContracts.Parse($"{{\"Version\":1,\"LeaseId\":\"{id}\",\"LoopbackPort\":10808,\"PeerAddresses\":[\"192.0.2.1\"]}}"));
            ExpectUserError(() => NetworkHelperContracts.Parse($"{{\"Version\":1,\"Version\":1,\"LeaseId\":\"{id}\",\"Action\":\"Acquire\",\"LoopbackPort\":10808,\"PeerAddresses\":[\"192.0.2.1\"]}}"));
            ExpectUserError(() => NetworkHelperContracts.Validate(new(2, id, NetworkLeaseAction.Acquire, 10808, new[] { "192.0.2.1" })));
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, id, (NetworkLeaseAction)99, 10808, new[] { "192.0.2.1" })));
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, id, NetworkLeaseAction.Release, 10808, new[] { "192.0.2.1" })));
            Assert(NetworkHelperContracts.Validate(new(1, id, NetworkLeaseAction.Release, 0, Array.Empty<string>())).Peers.Count == 0);
        }));
    }
}
