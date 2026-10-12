using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private static JsonObject NativeVless() => new()
    {
        ["type"] = "vless", ["server"] = "example.invalid", ["server_port"] = 443,
        ["uuid"] = "11111111-1111-4111-8111-111111111111",
        ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "example.invalid" }
    };

    private static async Task SingBoxChecksAsync(Func<string, Func<Task>, Task> check, string executable, string root)
    {
        await check("Native sing-box imports retain core identity and reject unsafe or unknown settings", () => Sync(() =>
        {
            var outbound = NativeVless();
            var profile = Profiles.Parse(outbound.ToJsonString());
            Assert(profile.Core == CoreKind.SingBox);
            Assert(Profiles.Fingerprint(profile) != Profiles.Fingerprint(profile with { Core = CoreKind.Xray }));
            outbound["tls"]!["insecure"] = true;
            ExpectUserError(() => Profiles.Parse(outbound.ToJsonString()));
            outbound["tls"]!["insecure"] = false;
            outbound["tls"]!["certificate_path"] = "do-not-read.pem";
            ExpectUserError(() => Profiles.Parse(outbound.ToJsonString()));
            outbound["tls"]!.AsObject().Remove("certificate_path");
            outbound["detour"] = "another-proxy";
            ExpectUserError(() => Profiles.Parse(outbound.ToJsonString()));
        }));
        await check("Mixed native subscriptions retain Xray, sing-box VLESS and Hysteria2 independently", () => Sync(() =>
        {
            var hy2 = new JsonObject
            {
                ["type"] = "hysteria2", ["server"] = "example.invalid", ["server_port"] = 443,
                ["password"] = "synthetic-password", ["tls"] = new JsonObject { ["enabled"] = true }
            };
            var imports = new JsonArray(Profiles.Parse(link).Outbound.DeepClone(), NativeVless(), hy2);
            var result = Subscriptions.Parse(imports.ToJsonString());
            Assert(result.Profiles.Count == 3 && result.Invalid == 0);
            Assert(result.Profiles.Count(p => p.Core == CoreKind.SingBox) == 2);
        }));
        await check("Real sing-box validates native config through stdin and preserves port across core switch", async () =>
        {
            using var engine = new Engine(executable, root);
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await engine.StartAsync(Profiles.Parse(NativeVless().ToJsonString()), cancel.Token);
            var port = engine.Port;
            Assert(engine.State == ConnectionState.Running && engine.CoreLabel == "sing-box 1.14.3");
            engine.Stop();
            await engine.StartAsync(Profiles.Parse(link), cancel.Token);
            Assert(engine.Port == port && engine.CoreLabel == "Xray 26.9.9");
            engine.Stop();
            var invalid = NativeVless(); invalid["type"] = "unsupported-fixture";
            await ExpectUserErrorAsync(() => engine.StartAsync(new Profile("Invalid", "TCP", invalid, CoreKind.SingBox), cancel.Token));
            Assert(engine.ProcessId is null && engine.State == ConnectionState.Failed);
        });
        await check("Real sing-box SOCKS carries HTTP and never bypasses a stopped proxy", async () =>
        {
            using var engine = new Engine(new CoreAdapter(CoreKind.SingBox, Path.Combine(Path.GetDirectoryName(executable)!, "sing-box.exe")));
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var direct = new JsonObject { ["type"] = "direct" }; // Fixture only; public imports reject DIRECT.
            await engine.StartAsync(direct, cancel.Token);
            var port = engine.Port;
            var destination = new TcpListener(IPAddress.Loopback, 0); destination.Start();
            try
            {
                var url = new Uri($"http://127.0.0.1:{((IPEndPoint)destination.LocalEndpoint).Port}/");
                var serving = ServeOnceAsync(destination, cancel.Token);
                Assert((await engine.ProbeAsync(url, cancel.Token)).Success);
                await serving;
                engine.Stop();
                using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{port}"), UseProxy = true };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
                try { await client.GetAsync(url, cancel.Token); throw new InvalidOperationException("Stopped proxy bypassed"); }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
                Assert(!destination.Pending());
            }
            finally { destination.Stop(); }
        });
        await check("sing-box interoperates with Xray VLESS/TLS and verifies concurrent payloads",
            () => TunnelFixtureAsync(executable, root, "tcp", false, false, CoreKind.SingBox));
        await check("sing-box rejects a mismatched TLS name without reaching the destination",
            () => TunnelFixtureAsync(executable, root, "tcp", false, true, CoreKind.SingBox));
        await check("Hysteria2 QUIC/TLS carries concurrent payloads with certificate verification",
            () => Hysteria2FixtureAsync(executable, false));
        await check("Hysteria2 rejects an invalid password without contacting the destination",
            () => Hysteria2FixtureAsync(executable, true));
        await check("Force-quitting the synthetic controller terminates sing-box through Windows Job",
            () => SingBoxJobFixtureAsync(root));
    }
}
