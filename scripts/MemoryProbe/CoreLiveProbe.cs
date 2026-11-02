using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

internal static class CoreLiveProbe
{
    internal static async Task<int> RunAsync(string executable, string report, int steadySeconds = 0)
    {
        if (steadySeconds != 0 && steadySeconds is < 10 or > 1800) return 2;
        static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var p = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return p; }
        int apiPort;
        while (true)
        {
            apiPort = Port(); if (apiPort == 65535) continue;
            var candidate = new TcpListener(IPAddress.Loopback, apiPort + 1);
            try { candidate.Start(); break; } catch (SocketException) { } finally { candidate.Stop(); }
        }
        var mixedPort = Port();
        while (mixedPort == apiPort || mixedPort == apiPort + 1) mixedPort = Port();
        var secret = Guid.NewGuid().ToString("N");
        var config = JsonSerializer.Serialize(new
        {
            log = new { disabled = true },
            inbounds = new[] { new { type = "mixed", listen = "127.0.0.1", listen_port = mixedPort } },
            outbounds = new[] { new { type = "direct", tag = "user-direct" } },
            route = new { find_process = true, final = "user-direct" },
            experimental = new { clash_api = new { external_controller = $"127.0.0.1:{apiPort}", secret } },
            services = new[] { new { type = "oblachka-programs", tag = "program-statistics", listen = $"127.0.0.1:{apiPort + 1}", secret } }
        });
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "run", "-c", "stdin" }) start.ArgumentList.Add(arg);
        using var core = Process.Start(start)!;
        var stdout = core.StandardOutput.ReadToEndAsync(); var stderr = core.StandardError.ReadToEndAsync();
        await core.StandardInput.WriteAsync(config); core.StandardInput.Close();
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        var source = new TcpListener(IPAddress.Loopback, 0); source.Start();
        using var stop = new CancellationTokenSource();
        stop.CancelAfter(TimeSpan.FromSeconds(60 + 2 * steadySeconds));
        var sockets = new List<TcpClient>(); var peers = new List<TcpClient>();
        var echoTasks = new List<Task>(); var samples = new List<object>(); var batches = new List<object>();
        async Task Echo(TcpClient peer)
        {
            try { var stream = peer.GetStream(); var data = new byte[4096]; int n; while ((n = await stream.ReadAsync(data, stop.Token)) > 0) await stream.WriteAsync(data.AsMemory(0, n), stop.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            finally { peer.Dispose(); }
        }
        async Task Sample(string phase)
        {
            var runtime = await http.GetStringAsync($"http://127.0.0.1:{apiPort + 1}/runtime"); core.Refresh();
            using var doc = JsonDocument.Parse(runtime);
            samples.Add(new { Phase = phase, PrivateBytes = core.PrivateMemorySize64, WorkingSet = core.WorkingSet64, CpuMs = core.TotalProcessorTime.TotalMilliseconds, Runtime = doc.RootElement.Clone() });
        }
        try
        {
            var ready = false;
            for (var i = 0; i < 50 && !ready; i++)
            {
                if (core.HasExited) throw new InvalidOperationException(await stderr);
                try { using var response = await http.GetAsync($"http://127.0.0.1:{apiPort + 1}/programs"); ready = response.IsSuccessStatusCode; }
                catch (HttpRequestException) { }
                if (!ready) await Task.Delay(100);
            }
            if (!ready) throw new InvalidOperationException("Core did not become ready");
            await Task.Delay(300);
            await Sample("idle");
            var destination = ((IPEndPoint)source.LocalEndpoint).Port;
            for (var i = 0; i < 500; i++)
            {
                var accepted = source.AcceptTcpClientAsync(stop.Token);
                var tcp = new TcpClient(); sockets.Add(tcp); await tcp.ConnectAsync(IPAddress.Loopback, mixedPort, stop.Token);
                var stream = tcp.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, stop.Token); var auth = new byte[2]; await stream.ReadExactlyAsync(auth, stop.Token);
                await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(destination >> 8), (byte)destination });
                var reply = new byte[10]; await stream.ReadExactlyAsync(reply, stop.Token); if (reply[1] != 0) throw new IOException("SOCKS connect failed");
                var peer = await accepted; peers.Add(peer); echoTasks.Add(Echo(peer));
                await stream.WriteAsync(new byte[1024], stop.Token); var received = new byte[1024]; await stream.ReadExactlyAsync(received, stop.Token);
            }
            await Task.Delay(100);
            using var programDoc = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{apiPort + 1}/programs"));
            var programRows = programDoc.RootElement.GetProperty("programs");
            var observedUp = programRows.EnumerateArray().Sum(p => p.GetProperty("upload").GetInt64());
            var observedDown = programRows.EnumerateArray().Sum(p => p.GetProperty("download").GetInt64());
            if (programRows.GetArrayLength() == 0 || observedUp < 500 * 1024 || observedDown < 500 * 1024) throw new InvalidOperationException("Core did not attribute the echo traffic to its client program");
            await Sample("500-open-connections");
            foreach (var endpoint in new[] { $"http://127.0.0.1:{apiPort}/connections", $"http://127.0.0.1:{apiPort + 1}/programs" })
                for (var i = 0; i < 5; i++) await http.GetByteArrayAsync(endpoint);
            if (steadySeconds > 0)
                await CoreSteadyProbe.RunAsync(core, http, apiPort + 1, sockets, steadySeconds, Sample, batches);
            else for (var round = 0; round < 3; round++)
            {
                foreach (var (name, endpoint) in new[] { ("FullConnections", $"http://127.0.0.1:{apiPort}/connections"), ("CompactPrograms", $"http://127.0.0.1:{apiPort + 1}/programs") })
                {
                    using var before = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{apiPort + 1}/runtime"));
                    core.Refresh(); var cpu = core.TotalProcessorTime.TotalMilliseconds; var watch = Stopwatch.StartNew(); int bytes = 0;
                    for (var i = 0; i < 100; i++) bytes = (await http.GetByteArrayAsync(endpoint)).Length;
                    watch.Stop(); core.Refresh(); var used = core.TotalProcessorTime.TotalMilliseconds - cpu;
                    using var after = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{apiPort + 1}/runtime"));
                    batches.Add(new { Name = name, Round = round, Iterations = 100, ResponseBytes = bytes, ElapsedMs = watch.Elapsed.TotalMilliseconds, CoreCpuMs = used,
                        CoreAllocatedBytes = after.RootElement.GetProperty("totalAlloc").GetUInt64() - before.RootElement.GetProperty("totalAlloc").GetUInt64() });
                }
            }
            await Sample("after-polling");
            foreach (var socket in sockets) socket.Dispose(); sockets.Clear();
            await Task.Delay(2000);
            await http.GetByteArrayAsync($"http://127.0.0.1:{apiPort + 1}/programs");
            await Sample("after-connections-close");
            File.WriteAllText(report, JsonSerializer.Serialize(new { Tun = false, Network = "Loopback only; 500 SOCKS/TCP echo connections", GOGC = Environment.GetEnvironmentVariable("GOGC") ?? "default", GOMEMLIMIT = Environment.GetEnvironmentVariable("GOMEMLIMIT") ?? "default", ProgramCount = programRows.GetArrayLength(), ObservedUpload = observedUp, ObservedDownload = observedDown, ProbeForcedGc = false, CoreStartupFreeOsMemory = true, Samples = samples, Batches = batches }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally
        {
            stop.Cancel(); source.Stop(); foreach (var socket in sockets) socket.Dispose(); foreach (var peer in peers) peer.Dispose();
            await Task.WhenAll(echoTasks);
            if (!core.HasExited) { core.Kill(); await core.WaitForExitAsync(); }
        }
    }
}
