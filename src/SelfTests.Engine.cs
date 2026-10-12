using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RoutePilot;

public static partial class SelfTests
{
    private static async Task EngineChecksAsync(Func<string, Func<Task>, Task> CheckAsync, string executable, string root)
    {
        using var engine = new Engine(executable, Path.Combine(root, "runtime"));
        await CheckAsync("Real Xray validates a synthetic TLS XHTTP profile", async () =>
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await engine.StartAsync(Profiles.Parse(link).Outbound, cancel.Token);
            Assert(engine.ProcessId is not null && engine.State == ConnectionState.Running);
            engine.Stop();
        });
        await CheckAsync("Real Xray rejects invalid config without writing profile files", async () =>
        {
            var outbound = Direct(); outbound["protocol"] = "not-a-protocol";
            try { await engine.StartAsync(outbound, CancellationToken.None); throw new InvalidOperationException(); }
            catch (UserError) { }
            Assert(engine.ProcessId is null && !Directory.EnumerateFiles(root, "config.json", SearchOption.AllDirectories).Any());
            engine.Stop();
        });
        await CheckAsync("HTTP travels through real SOCKS; config stays in memory; Stop terminates process", async () =>
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
            var destination = ((IPEndPoint)server.LocalEndpoint).Port;
            var serve = ServeOnceAsync(server, cancel.Token);
            try
            {
                await engine.StartAsync(Direct(), cancel.Token);
                var pid = engine.ProcessId!.Value;
                Assert(!Directory.EnumerateFiles(root, "config.json", SearchOption.AllDirectories).Any());
                var result = await engine.ProbeAsync(new Uri($"http://127.0.0.1:{destination}/"), cancel.Token);
                Assert(result.Success && engine.State == ConnectionState.Verified);
                await serve;
                engine.Stop();
                Assert(!IsAlive(pid));
            }
            finally { cancel.Cancel(); server.Stop(); engine.Stop(); }
        });
        await CheckAsync("Stopped SOCKS never falls back to a reachable direct destination", async () =>
        {
            var target = new TcpListener(IPAddress.Loopback, 0); target.Start();
            var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;
            await engine.StartAsync(Direct(), CancellationToken.None);
            var socksPort = engine.Port; engine.Stop();
            using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}", false), UseProxy = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                try { await http.GetAsync($"http://127.0.0.1:{targetPort}/"); throw new InvalidOperationException(); }
                catch (HttpRequestException) { }
                Assert(!target.Pending());
            }
            finally { target.Stop(); }
        });
        await CheckAsync("Engine crash is reported and cleanup allows restart", async () =>
        {
            await engine.StartAsync(Direct(), CancellationToken.None);
            var previousPort = engine.Port;
            var signaled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(ConnectionState state, string _) { if (state == ConnectionState.Failed) signaled.TrySetResult(); }
            engine.Changed += Changed;
            try
            {
                using var p = Process.GetProcessById(engine.ProcessId!.Value); p.Kill();
                await signaled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                engine.Stop();
                await engine.StartAsync(Direct(), CancellationToken.None);
                Assert(engine.State == ConnectionState.Running && engine.Port == previousPort);
            }
            finally { engine.Changed -= Changed; engine.Stop(); }
        });
        await CheckAsync("Cancel startup leaves no process or config", async () =>
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await engine.StartAsync(Direct(), cancel.Token); throw new InvalidOperationException(); }
            catch (OperationCanceledException) { }
            Assert(engine.ProcessId is null);
            Assert(!Directory.EnumerateFiles(root, "config.json", SearchOption.AllDirectories).Any());
        });
        await CheckAsync("Occupied stable SOCKS port fails explicitly without choosing another endpoint", async () =>
        {
            using var local = new Engine(executable, Path.Combine(root, "occupied-port"));
            await local.StartAsync(Direct(), CancellationToken.None);
            var port = local.Port; local.Stop();
            var occupied = new TcpListener(IPAddress.Loopback, port); occupied.Start();
            try
            {
                await ExpectUserErrorAsync(() => local.StartAsync(Direct(), CancellationToken.None));
                Assert(local.ProcessId is null && local.Port == 0 && local.State == ConnectionState.Failed);
            }
            finally { occupied.Stop(); }
        });
        await CheckAsync("Force-quitting controller terminates its Xray via Windows Job", async () =>
        {
            var childFolder = Path.Combine(root, "child"); Directory.CreateDirectory(childFolder);
            var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RoutePilot.exe")) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--job-child"); info.ArgumentList.Add(childFolder);
            using var child = Process.Start(info)!;
            var ready = Path.Combine(childFolder, "pid.txt");
            try
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (!File.Exists(ready)) { if (child.HasExited) throw new InvalidOperationException(); await Task.Delay(100, cancel.Token); }
                var pid = int.Parse(await File.ReadAllTextAsync(ready, cancel.Token));
                Assert(IsAlive(pid)); child.Kill(); await child.WaitForExitAsync(cancel.Token);
                for (var i = 0; i < 30 && IsAlive(pid); i++) await Task.Delay(100, cancel.Token);
                Assert(!IsAlive(pid));
                Assert(!Directory.EnumerateFiles(childFolder, "config.json", SearchOption.AllDirectories).Any());
            }
            finally { if (!child.HasExited) child.Kill(true); }
        });
        await CheckAsync("End-to-end VLESS TCP/TLS: verified certificate and concurrent payload integrity", () => TunnelFixtureAsync(executable, root, "tcp", false, false));
        await CheckAsync("End-to-end VLESS XHTTP/TLS: verified certificate and concurrent payload integrity", () => TunnelFixtureAsync(executable, root, "xhttp", false, false));
        await CheckAsync("VLESS rejects an invalid user without contacting the destination", () => TunnelFixtureAsync(executable, root, "tcp", true, false));
        await CheckAsync("TLS server name mismatch fails without contacting the destination", () => TunnelFixtureAsync(executable, root, "tcp", false, true));
        await CheckAsync("XHTTP rejects an invalid VLESS user without contacting destination", () => TunnelFixtureAsync(executable, root, "xhttp", true, false));
    }
}
