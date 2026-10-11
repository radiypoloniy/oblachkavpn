using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace RoutePilot;

public static partial class SelfTests
{
    private static async Task Hysteria2FixtureAsync(string executable, bool wrongPassword)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var adapter = new CoreAdapter(CoreKind.SingBox, Path.Combine(Path.GetDirectoryName(executable)!, "sing-box.exe"));
        using var server = new Engine(adapter);
        using var client = new Engine(adapter);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var allocator = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var quicPort = ((IPEndPoint)allocator.Client.LocalEndPoint!).Port;
        allocator.Close();
        await server.StartCoreAsync(port =>
        {
            var config = adapter.RuntimeConfig(new JsonObject { ["type"] = "direct" }, port);
            config["inbounds"]!.AsArray().Add(new JsonObject
            {
                ["type"] = "hysteria2", ["tag"] = "fixture", ["listen"] = "127.0.0.1", ["listen_port"] = quicPort,
                ["users"] = new JsonArray(new JsonObject { ["password"] = "synthetic-password" }),
                ["tls"] = new JsonObject
                {
                    ["enabled"] = true, ["certificate"] = new JsonArray(certificate.ExportCertificatePem()),
                    ["key"] = new JsonArray(key.ExportPkcs8PrivateKeyPem())
                }
            });
            return config;
        }, budget.Token);
        var outbound = new JsonObject
        {
            ["type"] = "hysteria2", ["server"] = "127.0.0.1", ["server_port"] = quicPort,
            ["password"] = wrongPassword ? "wrong-password" : "synthetic-password",
            ["tls"] = new JsonObject
            {
                ["enabled"] = true, ["server_name"] = "localhost", ["certificate"] = new JsonArray(certificate.ExportCertificatePem())
            }
        };
        await client.StartAsync(Profiles.Parse(outbound.ToJsonString()), budget.Token);
        var destination = new TcpListener(IPAddress.Loopback, 0); destination.Start();
        try
        {
            var url = new Uri($"http://127.0.0.1:{((IPEndPoint)destination.LocalEndpoint).Port}/");
            Task? serving = wrongPassword ? null : ServeOnceAsync(destination, budget.Token);
            var result = await client.ProbeAsync(url, budget.Token);
            if (wrongPassword) Assert(!result.Success && !destination.Pending());
            else
            {
                Assert(result.Success); await serving!;
                var payload = new byte[256 * 1024]; RandomNumberGenerator.Fill(payload);
                var responses = ServePayloadsAsync(destination, payload, 3, budget.Token);
                using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{client.Port}"), UseProxy = true };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
                {
                    var received = await http.GetByteArrayAsync(url, budget.Token);
                    Assert(received.AsSpan().SequenceEqual(payload));
                }));
                await responses;
            }
        }
        finally { budget.Cancel(); destination.Stop(); }
    }
}
