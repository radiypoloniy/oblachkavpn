namespace Oblachka;

public static partial class SelfTests
{
    private sealed class TunnelHostFixture : IClientTunnelHost
    {
        public bool IsService => true;
        public bool Foreign, FailLists, FailAcquire, FailRecover;
        public int Connects, Disposes;
        public Func<CancellationToken, Task>? Preparing, Listing, Dns;
        public readonly List<string> Calls = new();
        public readonly List<NetworkLeaseRequest> Requests = new();
        public readonly List<CompiledRouting> Lists = new();
        public readonly List<string[]> Domains = new();
        public FixtureLink? LastLink;
        public void CheckForeign() { if (Foreign) throw new UserError("fixture: another VPN"); }
        public Task RecoverIfNeededAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls.Add("recover"); if (FailRecover) throw new UserError("fixture recovery failure"); return Task.CompletedTask; }
        public async Task<IReadOnlyList<TunnelEndpointPlan>> PrepareAsync(IReadOnlyList<Profile> profiles, CancellationToken token)
        {
            Calls.Add("prepare"); if (Preparing is not null) await Preparing(token);
            token.ThrowIfCancellationRequested(); return profiles.Select(p => new TunnelEndpointPlan(p, ["192.0.2.1"])).ToArray();
        }
        public Task<IClientTunnelLink> ConnectAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add("connect"); Connects++;
            LastLink = new(this); return Task.FromResult<IClientTunnelLink>(LastLink);
        }

        public sealed class FixtureLink : IClientTunnelLink
        {
            private readonly TunnelHostFixture host;
            public FixtureClient Fixture { get; }
            public FixtureLink(TunnelHostFixture host) { this.host = host; Fixture = new(host); }
            public INetworkHelperClient Client => Fixture;
            public IHelperLifetime? Lifetime => null;
            public async Task SendListsAsync(CompiledRouting routing, CancellationToken token)
            {
                host.Calls.Add("lists"); if (host.FailLists) throw new UserError("fixture: old helper");
                host.Lists.Add(routing); if (host.Listing is not null) await host.Listing(token);
            }
            public async Task<bool> SyncDnsAsync(string[] domains, CoreStatsEntry? stats, CancellationToken token)
            {
                host.Calls.Add("dns"); if (host.Dns is not null) await host.Dns(token);
                token.ThrowIfCancellationRequested(); host.Domains.Add(domains); return true;
            }
            public async ValueTask DisposeAsync() { host.Disposes++; await Fixture.DisposeAsync(); }
        }
        public sealed class FixtureClient(TunnelHostFixture host) : INetworkHelperClient
        {
            public bool Lost;
            public int Subscribers => (ConnectionLost?.GetInvocationList().Length ?? 0) + (ProtectionRepaired?.GetInvocationList().Length ?? 0);
            public event Action? ConnectionLost;
            public event Action? ProtectionRepaired;
            public void Lose() { Lost = true; ConnectionLost?.Invoke(); }
            public Task<bool> AcquireAsync(NetworkLeaseRequest request, CancellationToken token)
            {
                NetworkHelperContracts.Validate(request); host.Calls.Add("acquire"); host.Requests.Add(request);
                if (host.FailAcquire) throw new UserError("fixture acquire failure");
                return Task.FromResult(true);
            }
            public Task<NetworkSessionState> ReleaseAsync(CancellationToken token)
            {
                host.Calls.Add("release"); if (Lost) throw new IOException("fixture: lost pipe");
                return Task.FromResult(NetworkSessionState.Released);
            }
            public ValueTask DisposeAsync() { host.Calls.Add("dispose"); return ValueTask.CompletedTask; }
        }
    }
}
