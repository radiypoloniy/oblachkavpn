using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task TunnelFixtureAsync(string executable, string root, string network, bool wrongUser, bool wrongName, CoreKind core = CoreKind.Xray)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var enhanced = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(enhanced, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        JsonArray Lines(string pem) => new(pem.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => (JsonNode?)JsonValue.Create(s.Trim())).ToArray());
        var portListener = new TcpListener(IPAddress.Loopback, 0); portListener.Start();
        var inboundPort = ((IPEndPoint)portListener.LocalEndpoint).Port; portListener.Stop();
        const string user = "11111111-1111-4111-8111-111111111111";
        var stream = new JsonObject { ["network"] = network, ["security"] = "tls", ["tlsSettings"] = new JsonObject { ["certificates"] = new JsonArray(new JsonObject { ["certificate"] = Lines(cert.ExportCertificatePem()), ["key"] = Lines(rsa.ExportPkcs8PrivateKeyPem()) }) } };
        if (network == "xhttp") stream["xhttpSettings"] = new JsonObject { ["path"] = "/fixture", ["mode"] = "auto" };
        using var server = new Engine(executable, Path.Combine(root, "fixture-server"));
        using var client = new Engine(executable, Path.Combine(root, "fixture-client"));
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        server.FixtureTrace = line => trace.Enqueue("server: " + line);
        client.FixtureTrace = line => trace.Enqueue("client: " + line);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await server.StartCoreAsync(socksPort =>
        {
            var fixtureDirect = Direct();
            // Xray's public-server default blocks private destinations. Permit only our loopback fixture.
            fixtureDirect["settings"]!["finalRules"] = new JsonArray(new JsonObject { ["action"] = "allow", ["ip"] = new JsonArray("127.0.0.1/32") });
            var config = Profiles.RuntimeConfig(fixtureDirect, socksPort);
            config["log"]!["loglevel"] = "debug";
            config["inbounds"]!.AsArray().Add(new JsonObject { ["tag"] = "fixture", ["listen"] = "127.0.0.1", ["port"] = inboundPort, ["protocol"] = "vless",
                ["settings"] = new JsonObject { ["clients"] = new JsonArray(new JsonObject { ["id"] = user }), ["decryption"] = "none" }, ["streamSettings"] = stream });
            return config;
        }, cancel.Token);
        var id = wrongUser ? "22222222-2222-4222-8222-222222222222" : user;
        var sni = wrongName ? "wrong.invalid" : "localhost";
        var outbound = Profiles.Parse($"vless://{id}@127.0.0.1:{inboundPort}?security=tls&type={network}&sni={sni}&path=%2Ffixture").Outbound;
        outbound["streamSettings"]!["tlsSettings"]!["certificates"] = new JsonArray(new JsonObject { ["usage"] = "verify", ["certificate"] = Lines(cert.ExportCertificatePem()) });
        // Windows Xray ignores inline trust anchors unless this flag selects its private cert pool.
        // Certificate and hostname verification remain enabled; the OS trust store is untouched.
        outbound["streamSettings"]!["tlsSettings"]!["disableSystemRoot"] = true;
        if (core == CoreKind.SingBox)
        {
            outbound = new JsonObject
            {
                ["type"] = "vless", ["server"] = "127.0.0.1", ["server_port"] = inboundPort, ["uuid"] = id,
                ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = sni, ["certificate"] = Lines(cert.ExportCertificatePem()) }
            };
        }
        var destination = new TcpListener(IPAddress.Loopback, 0); destination.Start();
        try
        {
            await client.StartAsync(new Profile("Fixture", network, outbound, core), cancel.Token);
            var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
            Task? serve = !wrongUser && !wrongName ? ServeOnceAsync(destination, cancel.Token) : null;
            var result = await client.ProbeAsync(new Uri($"http://127.0.0.1:{destinationPort}/"), cancel.Token);
            if (wrongUser || wrongName) { Assert(!result.Success); Assert(!destination.Pending()); }
            else
            {
                if (!result.Success) throw new InvalidOperationException("Local TLS fixture failed: " + string.Join("\n", trace));
                await serve!;
                var payload = new byte[256 * 1024]; RandomNumberGenerator.Fill(payload);
                var serving = ServePayloadsAsync(destination, payload, 3, cancel.Token);
                using var handler = new SocketsHttpHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{client.Port}", false), UseProxy = true, AllowAutoRedirect = false, UseCookies = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                var requests = Enumerable.Range(0, 3).Select(async i =>
                {
                    var received = await http.GetByteArrayAsync($"http://127.0.0.1:{destinationPort}/payload/{i}", cancel.Token);
                    Assert(received.AsSpan().SequenceEqual(payload));
                }).ToArray();
                await Task.WhenAll(requests); await serving;
                var siteErrorResponse = ServeOnceAsync(destination, cancel.Token, 503);
                var siteError = await client.ProbeAsync(new Uri($"http://127.0.0.1:{destinationPort}/"), cancel.Token);
                await siteErrorResponse;
                Assert(!siteError.Success && siteError.Failure == ProbeFailure.TargetResponse && client.State == ConnectionState.Running);
                var stablePort = client.Port;
                var rejected = (JsonObject)outbound.DeepClone(); rejected[core == CoreKind.Xray ? "protocol" : "type"] = "invalid-fixture-protocol";
                var nextRequest = ServeOnceAsync(destination, cancel.Token);
                var found = await new RouteSearch(client).FindAsync(new[] { new Profile("Rejected", network, rejected, core), new Profile("Working", network, outbound, core) }, 0,
                    new Uri($"http://127.0.0.1:{destinationPort}/"), cancel.Token);
                await nextRequest;
                Assert(found.ProfileIndex == 1 && found.Attempts.Count == 2 && client.State == ConnectionState.Verified && client.Port == stablePort);
            }
        }
        finally { cancel.Cancel(); destination.Stop(); }
    }
    private static async Task ServeOnceAsync(TcpListener server, CancellationToken token, int status = 204)
    {
        using var client = await server.AcceptTcpClientAsync(token);
        var stream = client.GetStream(); var request = new List<byte>(); var buffer = new byte[1024];
        while (!Encoding.ASCII.GetString(request.ToArray()).Contains("\r\n\r\n"))
        {
            var n = await stream.ReadAsync(buffer, token);
            if (n == 0 || request.Count > 8192) throw new IOException();
            request.AddRange(buffer.Take(n));
        }
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);
    }
    private static async Task ServePayloadsAsync(TcpListener server, byte[] payload, int count, CancellationToken token)
    {
        async Task Respond(TcpClient accepted)
        {
            using var client = accepted;
            var stream = client.GetStream(); var header = new List<byte>(); var buffer = new byte[1024];
            while (!Encoding.ASCII.GetString(header.ToArray()).Contains("\r\n\r\n"))
            {
                var n = await stream.ReadAsync(buffer, token);
                if (n == 0 || header.Count > 8192) throw new IOException();
                header.AddRange(buffer.Take(n));
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"), token);
            await stream.WriteAsync(payload, token);
        }
        var responses = new List<Task>();
        for (var i = 0; i < count; i++) responses.Add(Respond(await server.AcceptTcpClientAsync(token)));
        await Task.WhenAll(responses);
    }
}
