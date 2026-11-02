using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class HysteriaCoreProbe
{
    internal static async Task<int> RunAsync(string nativeCore, string singBox, string report)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=fixture.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("fixture.invalid"); request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pem = certificate.ExportCertificatePem(); var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var serverPort = HysteriaProbeProcess.Port(udp: true);
        var serverConfig = Config(new JsonArray(new JsonObject
        {
            ["type"] = "hysteria2", ["listen"] = "127.0.0.1", ["listen_port"] = serverPort,
            ["users"] = new JsonArray(new JsonObject { ["password"] = password }),
            ["tls"] = new JsonObject { ["enabled"] = true, ["certificate"] = new JsonArray(pem), ["key"] = new JsonArray(key.ExportPkcs8PrivateKeyPem()) }
        }), new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "exit" }), "exit");
        await using var server = await HysteriaProbeProcess.StartAsync(singBox, serverConfig, stop.Token);
        var source = new TcpListener(IPAddress.Loopback, 0); source.Start();
        var echoing = EchoAsync(source, stop.Token); var rounds = new List<object>();
        try
        {
            for (var round = 0; round < 3; round++)
                foreach (var single in new[] { false, true })
                {
                    var proxyPort = HysteriaProbeProcess.Port(); HysteriaProbeProcess? window = null;
                    try
                    {
                        if (!single)
                        {
                            var windowConfig = ClientConfig(proxyPort, Hysteria(serverPort, password), pem);
                            window = await HysteriaProbeProcess.StartAsync(singBox, windowConfig, stop.Token);
                            await window.ReadyAsync(proxyPort, stop.Token);
                        }
                        var clientPort = HysteriaProbeProcess.Port();
                        var outbound = single ? Hysteria(serverPort, password) : new JsonObject
                            { ["type"] = "socks", ["server"] = "127.0.0.1", ["server_port"] = proxyPort };
                        var config = ClientConfig(clientPort, outbound, pem);
                        await using var core = await HysteriaProbeProcess.StartAsync(nativeCore, config, stop.Token);
                        await core.ReadyAsync(clientPort, stop.Token);
                        var connections = new List<TcpClient>();
                        try
                        {
                            var destination = ((IPEndPoint)source.LocalEndpoint).Port;
                            for (var i = 0; i < 100; i++) connections.Add(await OpenEchoAsync(clientPort, destination, stop.Token));
                            await Task.Delay(5000, stop.Token);
                            var memory = new List<object>();
                            for (var i = 0; i < 5; i++)
                            {
                                var tunnel = core.Memory(); var extra = window?.Memory();
                                memory.Add(new { Tunnel = tunnel, Window = extra,
                                    TotalPrivateBytes = tunnel.PrivateBytes + (extra?.PrivateBytes ?? 0),
                                    TotalPrivateWorkingSet = tunnel.PrivateWorkingSet + (extra?.PrivateWorkingSet ?? 0) });
                                await Task.Delay(1000, stop.Token);
                            }
                            rounds.Add(new { Round = round, Mode = single ? "one-core" : "two-cores", CoreCount = single ? 1 : 2,
                                Connections = connections.Count, VerifiedEchoBytes = connections.Count * 4096, Memory = memory });
                        }
                        finally { foreach (var connection in connections) connection.Dispose(); }
                    }
                    finally { if (window is not null) await window.DisposeAsync(); }
                }
            File.WriteAllText(report, JsonSerializer.Serialize(new { Network = "Loopback only; Hysteria2 TCP echo, no TUN or system changes",
                CertificateVerification = true, Trust = "Per-process fixture CA; Windows certificate stores unchanged", ForcedGc = false,
                ServerExcludedFromTotals = true, Rounds = rounds }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { stop.Cancel(); source.Stop(); await echoing; }
    }
    private static JsonObject Hysteria(int port, string password) => new()
    {
        ["type"] = "hysteria2", ["server"] = "127.0.0.1", ["server_port"] = port, ["password"] = password,
        ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "fixture.invalid", ["alpn"] = new JsonArray("h3") }
    };
    private static JsonObject Config(JsonArray inbounds, JsonArray outbounds, string final) => new()
    {
        ["log"] = new JsonObject { ["disabled"] = true }, ["inbounds"] = inbounds, ["outbounds"] = outbounds,
        ["route"] = new JsonObject { ["find_process"] = true, ["final"] = final }
    };
    private static JsonObject ClientConfig(int port, JsonObject outbound, string certificate)
    {
        outbound["tag"] = "server";
        var config = Config(new JsonArray(new JsonObject { ["type"] = "mixed", ["listen"] = "127.0.0.1", ["listen_port"] = port }), new JsonArray(outbound), "server");
        config["certificate"] = new JsonObject { ["store"] = "none", ["certificate"] = new JsonArray(certificate) };
        return config;
    }
    private static async Task<TcpClient> OpenEchoAsync(int proxy, int target, CancellationToken token)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, proxy, token); var stream = tcp.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, token); var auth = new byte[2]; await stream.ReadExactlyAsync(auth, token);
            if (auth[0] != 5 || auth[1] != 0) throw new IOException("SOCKS authentication failed");
            await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(target >> 8), (byte)target }, token);
            var response = new byte[10]; await stream.ReadExactlyAsync(response, token);
            if (response[1] != 0) throw new IOException("SOCKS destination failed");
            var payload = RandomNumberGenerator.GetBytes(4096); var echo = new byte[payload.Length];
            await stream.WriteAsync(payload, token); await stream.ReadExactlyAsync(echo, token);
            if (!payload.AsSpan().SequenceEqual(echo)) throw new IOException("Echo mismatch");
            return tcp;
        }
        catch { tcp.Dispose(); throw; }
    }
    private static async Task EchoAsync(TcpListener listener, CancellationToken token)
    {
        try { while (!token.IsCancellationRequested) { var peer = await listener.AcceptTcpClientAsync(token); _ = CopyAsync(peer, token); } }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
    }
    private static async Task CopyAsync(TcpClient peer, CancellationToken token)
    {
        using (peer)
        {
            try { var stream = peer.GetStream(); var bytes = new byte[16384]; int n; while ((n = await stream.ReadAsync(bytes, token)) > 0) await stream.WriteAsync(bytes.AsMemory(0, n), token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }
}
