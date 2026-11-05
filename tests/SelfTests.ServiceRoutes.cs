using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ServiceRouteChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var profiles = new[] { "203.0.113.7", "198.51.100.9" }.Select(ip =>
            Profiles.Parse($"trojan://fixture-secret@{ip}:443?sni=example.com#Fixture")).ToArray();
        var peers = new[] { "203.0.113.7", "198.51.100.9" };
        var plans = profiles.Select((p, i) => new TunnelEndpointPlan(p, [peers[i]])).ToArray();
        var exits = profiles.Select(DirectOutbound.From).ToArray();
        ServiceRoute[] routes =
        [
            new("claude", ClientServerCatalog.Id(profiles[0])!, ["claude.fixture.invalid"], []),
            new("codex", ClientServerCatalog.Id(profiles[1])!, ["codex.fixture.invalid"], []),
            new("missing", new string('A', 64), ["missing.fixture.invalid"], [])
        ];
        var pinned = ServiceRoutePlan.Bind(routes, plans, exits, null);
        var request = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, peers,
            Stats: CoreStatsEntry.Create(Engine.FreePort()), Direct: exits) { ServiceRoutes = pinned };
        await check("Service preset saves a scoped rule and reports overlapping user rules", () =>
        {
            using var owner = new ClientRuntime(new ConnectionCore());
            owner.Persistence.Configure(null, isolated: true);
            owner.Profiles.Imported = profiles;
            var preset = ClientServiceRoutes.Presets.Single(p => p.Id == "claude");
            var file = ProgramRoutingPlan.Create(@"C:\Tools\Claude\Claude.exe", SiteAction.Tunnel, allInFolder: false);
            var draft = owner.ServiceRoutes.Draft(preset, profiles[0], [file]);
            owner.TunnelPolicy.Sites.Add(RoutingPlan.Create("api.anthropic.com", SiteAction.Direct));
            owner.TunnelPolicy.Programs.Add(ProgramRoutingPlan.Create("chrome.exe", SiteAction.Tunnel));
            Assert(owner.ServiceRoutes.Conflicts(draft).Length == 2);
            owner.ServiceRoutes.Save(draft);
            Assert(owner.TunnelPolicy.ServiceRoutes.Single() == draft);
            var changed = owner.ServiceRoutes.Draft(preset, profiles[1], []);
            owner.ServiceRoutes.Save(changed);
            Assert(owner.TunnelPolicy.ServiceRoutes.Single() == changed);
            owner.ServiceRoutes.Remove(draft.Id);
            Assert(owner.TunnelPolicy.ServiceRoutes.Count == 0);
            return Task.CompletedTask;
        });
        await check("Service routes bind by profile identity and cross the helper boundary", async () =>
        {
            Assert(pinned.Select(r => r.ExitIndex).SequenceEqual(new int?[] { 0, 1, null }));
            Assert(ServiceRoutePlan.Bind(routes, plans.Reverse().ToArray(), exits.Reverse().ToArray(), null)
                .Select(r => r.ExitIndex).SequenceEqual(new int?[] { 1, 0, null }));
            Assert(ServiceRoutePlan.Bind(routes, plans, exits, p => p != profiles[0])[0].ExitIndex is null);
            var lease = NetworkHelperContracts.Validate(request);
            Assert(SingBoxTunnelProcess.RequestFor(lease).ServiceRoutes!.SequenceEqual(pinned));
            ExpectUserError(() => NetworkHelperContracts.Validate(request with { ServiceRoutes = [pinned[0] with { ExitIndex = 2 }] }));
            ExpectUserError(() => NetworkHelperContracts.Validate(request with { ServiceRoutes = [pinned[0], pinned[1] with { Domains = pinned[0].Domains }] }));
            var file = ProgramRoutingPlan.Create(@"C:\Tools\Claude\Claude.exe", SiteAction.Tunnel, allInFolder: false);
            await CoreConfigurationCheck.ValidateTunAsync(request with
            {
                ServiceRoutes = [pinned[0] with { Files = [file] }, pinned[1], pinned[2]]
            }, CancellationToken.None);
        });
        await check("Two pinned exits work concurrently and a dead exit cannot fall back", async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var token = deadline.Token;
            using var claude = NewListener();
            using var codex = NewListener();
            using var ordinary = NewListener();
            var ingress = Engine.FreePort();
            var config = SingBoxTunConfig.Create(request);
            config["inbounds"] = new JsonArray(new JsonObject
            {
                ["type"] = "socks", ["tag"] = "system-tun", ["listen"] = "127.0.0.1", ["listen_port"] = ingress
            });
            config["route"]!["auto_detect_interface"] = false;
            var outbounds = config["outbounds"]!.AsArray();
            ReplaceExit("direct-0", claude);
            ReplaceExit("direct-1", codex);
            ReplaceExit("local-core", ordinary);
            var start = new ProcessStartInfo(TunCore.Program())
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            foreach (var arg in new[] { "run", "-c", "stdin" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            using var job = new ProcessJob();
            job.Attach(process);
            process.BeginOutputReadLine();
            var errors = process.StandardError.ReadToEndAsync(token);
            await process.StandardInput.WriteAsync(config.ToJsonString());
            process.StandardInput.Close();
            var stats = request.Stats!;
            while (!await CoreSelector.SelectAsync(stats, "local-core", token))
            {
                if (process.HasExited) throw new InvalidOperationException(await errors);
                await Task.Delay(100, token);
            }
            using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{ingress}") });
            await Task.WhenAll(Verify("claude", claude), Verify("codex", codex), Verify("ordinary", ordinary));
            var probe = new Uri("http://probe.fixture.invalid/");
            var probeResponse = ServeServiceExitAsync(claude, "probe", token);
            Assert(await CoreSelector.DelayAsync(stats, "direct-0", probe, token) is > 0);
            await probeResponse;
            claude.Stop();
            Assert(await CoreSelector.DelayAsync(stats, "direct-0", probe, token) is null);
            await Task.WhenAll(Verify("codex", codex), Verify("ordinary", ordinary), Blocked("claude"), Blocked("missing"));

            void ReplaceExit(string tag, TcpListener endpoint)
            {
                var existing = outbounds.Single(o => o?["tag"]?.GetValue<string>() == tag);
                outbounds[outbounds.IndexOf(existing)] = new JsonObject
                {
                    ["type"] = "socks", ["tag"] = tag, ["server"] = "127.0.0.1",
                    ["server_port"] = ((IPEndPoint)endpoint.LocalEndpoint).Port
                };
            }
            Task<string> Get(string service) => client.GetStringAsync($"http://{service}.fixture.invalid/", token);
            async Task Verify(string service, TcpListener endpoint)
            {
                var response = ServeServiceExitAsync(endpoint, service, token);
                Assert(await Get(service) == service);
                await response;
            }
            async Task Blocked(string service)
            {
                try { await Get(service); }
                catch (HttpRequestException) { return; }
                throw new InvalidOperationException($"Unreachable service {service} received a response.");
            }
        });
    }

    private static TcpListener NewListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static async Task ServeServiceExitAsync(TcpListener listener, string response, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        using var stream = client.GetStream();
        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, token);
        await stream.ReadExactlyAsync(new byte[greeting[1]], token);
        await stream.WriteAsync(new byte[] { 5, 0 }, token);
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = header[3] switch { 1 => 4, 4 => 16, _ => stream.ReadByte() };
        await stream.ReadExactlyAsync(new byte[length + 2], token);
        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, token);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(token) is { Length: > 0 }) { }
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n{response}"), token);
    }
}
