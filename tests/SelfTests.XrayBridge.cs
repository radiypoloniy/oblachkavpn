using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private const string xhttpLink = "vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=reality&sni=www.example.com&fp=chrome&pbk=SbVKOEMjK0sIlbwg4akyBg5mL5KZwwB-ed4eEE7YnRc&sid=6ba85179e30d4fc2&type=xhttp&path=%2Fx&mode=auto#X";

    private static JsonObject XhttpBridged()
    {
        var profile = Profiles.Parse(xhttpLink);
        var xhttp = profile.Outbound["streamSettings"]!["xhttpSettings"]!.AsObject();
        xhttp["extra"] = new JsonObject
        {
            ["xPaddingBytes"] = "100-1000", ["noGRPCHeader"] = false, ["headers"] = new JsonObject { ["User-Agent"] = "Mozilla/5.0" },
            ["xmux"] = new JsonObject { ["maxConcurrency"] = "16-32", ["hMaxRequestTimes"] = "600-900" }
        };
        // Поля подписок, которые на путь трафика не влияют: окно их снимает.
        var user = profile.Outbound["settings"]!["vnext"]![0]!["users"]![0]!.AsObject();
        user["level"] = 8; user["email"] = "fixture@example.com";
        profile.Outbound["mux"] = new JsonObject { ["enabled"] = false, ["concurrency"] = -1 };
        profile.Outbound["streamSettings"]!["sockopt"] = new JsonObject { ["tcpFastOpen"] = true };
        return DirectOutbound.From(profile)!;
    }

    private static async Task XrayBridgeChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var peers = new[] { IPAddress.Parse("203.0.113.7") };
        await check("XHTTP profiles reach the tunnel core as rebuilt Xray outbounds that the pinned tunnel core accepts", async () =>
        {
            var outbound = XhttpBridged();
            Assert(outbound["type"]!.GetValue<string>() == "xray");
            var xray = outbound["xray"]!.AsObject();
            var user = xray["settings"]!["vnext"]![0]!["users"]![0]!.AsObject();
            Assert(xray["tag"] is null && xray["mux"] is null && xray["streamSettings"]!["sockopt"] is null && user["level"] is null && user["email"] is null);
            Assert(xray["streamSettings"]!["xhttpSettings"]!["extra"]!["xmux"]!["maxConcurrency"]!.GetValue<string>() == "16-32");
            // Служба принимает собранный окном выход без изменений — и собирает его заново сама.
            Assert(JsonNode.DeepEquals(DirectOutboundPolicy.Validate(outbound, peers), outbound));
            var ech = DirectOutbound.From(Profiles.Parse("vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=tls&sni=example.com&type=ws&path=%2Fws%3Fed%3D2048&ech=AEX%2B#ECH"))!;
            foreach (var accepted in new[] { outbound, ech })
            {
                var tagged = accepted.DeepClone().AsObject(); tagged["tag"] = "direct-0";
                var config = new JsonObject { ["log"] = new JsonObject { ["disabled"] = true }, ["outbounds"] = new JsonArray(tagged) };
                await CoreConfigurationCheck.ValidateAsync(new(CoreKind.SingBox, TunCore.Program()), config, CancellationToken.None);
            }
        });
        await check("Service rejects Xray outbounds that could leave the session servers or weaken TLS", () => Sync(() =>
        {
            var good = XhttpBridged();
            JsonObject With(Action<JsonObject> change) { var copy = good.DeepClone().AsObject(); change(copy["xray"]!.AsObject()); return copy; }
            JsonObject Stream(JsonObject x) => x["streamSettings"]!.AsObject();
            JsonObject Xhttp(JsonObject x) => Stream(x)["xhttpSettings"]!.AsObject();
            var bad = new[]
            {
                With(x => x["settings"]!["vnext"]![0]!["address"] = "198.51.100.9"),
                With(x => x["settings"]!["vnext"]![0]!["address"] = "node.example.com"),
                With(x => Stream(x)["sockopt"] = new JsonObject { ["interface"] = "Ethernet" }),
                With(x => Xhttp(x)["extra"]!["downloadSettings"] = new JsonObject { ["address"] = "203.0.113.7", ["port"] = 443 }),
                With(x => Xhttp(x)["mode"] = "bogus"),
                With(x => Xhttp(x)["path"] = "x"),
                With(x => Xhttp(x)["extra"]!["headers"]!["X-Bad"] = "a\r\nHost: evil"),
                With(x => Stream(x)["security"] = "tls"),
                With(x => Stream(x)["realitySettings"]!["publicKey"] = "not a key"),
                With(x => x["protocol"] = "vmess"),
                With(x => x["sendThrough"] = "0.0.0.0"),
                With(x => x["settings"]!["vnext"]![0]!["users"]![0]!["flow"] = "xtls-rprx-vision"),
                With(x => x["settings"]!["vnext"]!.AsArray().Add(x["settings"]!["vnext"]![0]!.DeepClone())),
                With(x => { Stream(x)["security"] = "tls"; Stream(x).Remove("realitySettings"); Stream(x)["tlsSettings"] = new JsonObject { ["serverName"] = "example.com", ["allowInsecure"] = true }; }),
                With(x => { Stream(x)["security"] = "tls"; Stream(x).Remove("realitySettings"); Stream(x)["tlsSettings"] = new JsonObject { ["serverName"] = "example.com", ["echConfigList"] = "example.com+https://1.1.1.1/dns-query" }; }),
                With(x => { Stream(x)["security"] = "tls"; Stream(x).Remove("realitySettings"); Stream(x)["tlsSettings"] = new JsonObject { ["serverName"] = "example.com", ["certificates"] = new JsonArray() }; })
            };
            foreach (var outbound in bad) ExpectUserError(() => DirectOutboundPolicy.Validate(outbound, peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(new JsonObject { ["type"] = "xray", ["xray"] = good["xray"]!.DeepClone(), ["detour"] = "x" }, peers));
            // Перенаправление сокета окно не переносит: профиль идёт прежним путём.
            var redirected = Profiles.Parse(xhttpLink); redirected.Outbound["streamSettings"]!["sockopt"] = new JsonObject { ["dialerProxy"] = "fragment" };
            Assert(DirectOutbound.From(redirected) is null);
        }));
        await check("Lease carries Xray outbounds into the tunnel config, and the tunnel core checks the whole TUN config", async () =>
        {
            var request = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["203.0.113.7"], Stats: CoreStatsEntry.Create(23998), Direct: [XhttpBridged()]);
            var lease = NetworkHelperContracts.Validate(request);
            Assert(SingBoxTunnelProcess.RequestFor(lease).Direct is [{ } carried] && carried["type"]!.GetValue<string>() == "xray");
            await CoreConfigurationCheck.ValidateTunAsync(request, CancellationToken.None);
            var direct = SingBoxTunConfig.Create(request)["outbounds"]!.AsArray().Single(o => o!["tag"]!.GetValue<string>() == SingBoxTunConfig.DirectTag(0))!;
            Assert(direct["type"]!.GetValue<string>() == "xray" && direct["xray"]!["protocol"]!.GetValue<string>() == "vless");
            // Аренда — одно сообщение канала: выходы сверх бюджета идут прежним путём, порядок сохраняется.
            var one = XhttpBridged(); var size = System.Text.Encoding.UTF8.GetByteCount(one.ToJsonString());
            var fit = DirectOutbound.Fit([one, null, XhttpBridged(), XhttpBridged()], size * 2);
            Assert(fit[0] is not null && fit[1] is null && fit[2] is not null && fit[3] is null);
            var sixteen = Enumerable.Range(0, DirectOutboundPolicy.MaxOutbounds).Select(_ => (JsonObject?)XhttpBridged()).ToArray();
            var full = request with { PeerAddresses = ["203.0.113.7"], Direct = DirectOutbound.Fit(sixteen) };
            Assert(System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(new HelperRequest(1, Guid.NewGuid(), HelperOperation.Acquire, full))) < HelperProtocol.MaxBytes);
        });
        await check("Tunnel core carries traffic over XHTTP to a real Xray server inside its own process", XhttpEndToEndAsync);
    }

    // Настоящие ядра на 127.0.0.1: Xray-сервер с входом XHTTP и ядро туннеля со входом mixed и
    // выходом "xray". Запрос идёт ядро туннеля → встроенный Xray → XHTTP → сервер → источник.
    private static async Task XhttpEndToEndAsync()
    {
        using var source = new HttpListener();
        var sourcePort = Engine.FreePort();
        source.Prefixes.Add($"http://127.0.0.1:{sourcePort}/");
        source.Start();
        _ = Task.Run(async () =>
        {
            while (source.IsListening)
            {
                try
                {
                    var context = await source.GetContextAsync();
                    var body = System.Text.Encoding.ASCII.GetBytes("oblachka-xhttp-ok");
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            }
        });
        var id = "6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60";
        var serverPort = Engine.FreePort();
        var server = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray(new JsonObject
            {
                ["listen"] = "127.0.0.1", ["port"] = serverPort, ["protocol"] = "vless",
                ["settings"] = new JsonObject { ["clients"] = new JsonArray(new JsonObject { ["id"] = id }), ["decryption"] = "none" },
                ["streamSettings"] = new JsonObject { ["network"] = "xhttp", ["xhttpSettings"] = new JsonObject { ["path"] = "/x" } }
            }),
            ["outbounds"] = new JsonArray(new JsonObject
            {
                ["protocol"] = "freedom",
                ["settings"] = new JsonObject { ["finalRules"] = new JsonArray(new JsonObject { ["ip"] = new JsonArray("127.0.0.1/32"), ["action"] = "allow" }) }
            })
        };
        var xray = DirectOutboundPolicy.Validate(new JsonObject
        {
            ["type"] = "xray",
            ["xray"] = new JsonObject
            {
                ["protocol"] = "vless",
                ["settings"] = new JsonObject { ["vnext"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = serverPort, ["users"] = new JsonArray(new JsonObject { ["id"] = id, ["encryption"] = "none" }) }) },
                ["streamSettings"] = new JsonObject { ["network"] = "xhttp", ["xhttpSettings"] = new JsonObject { ["path"] = "/x", ["mode"] = "auto" } }
            }
        }, [IPAddress.Loopback]);
        xray["tag"] = "direct-0";
        var mixedPort = Engine.FreePort();
        var client = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["inbounds"] = new JsonArray(new JsonObject { ["type"] = "mixed", ["listen"] = "127.0.0.1", ["listen_port"] = mixedPort }),
            ["outbounds"] = new JsonArray(xray)
        };
        using var job = new ProcessJob();
        Process Run(string executable, string[] arguments, JsonObject config)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in arguments) start.ArgumentList.Add(a);
            var process = Process.Start(start)!;
            job.Attach(process);
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            process.StandardInput.Write(config.ToJsonString()); process.StandardInput.Close();
            return process;
        }
        using var serverProcess = Run(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), ["run", "-c", "stdin:", "-format", "json"], server);
        using var coreProcess = Run(TunCore.Program(), ["run", "-c", "stdin"], client);
        try
        {
            string? body = null;
            for (var i = 0; i < 30 && body?.EndsWith("oblachka-xhttp-ok", StringComparison.Ordinal) != true; i++)
            {
                try { body = await SocksGetAsync(mixedPort, sourcePort); }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException) { await Task.Delay(200); }
            }
            Assert(body?.EndsWith("oblachka-xhttp-ok", StringComparison.Ordinal) == true);
        }
        finally
        {
            foreach (var process in new[] { coreProcess, serverProcess }) try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            source.Stop();
        }
    }

    // HTTP-запрос через SOCKS5 входа ядра: HttpClient к адресу 127.0.0.1 прокси не использует.
    private static async Task<string> SocksGetAsync(int socksPort, int targetPort)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, socksPort, timeout.Token);
        var stream = tcp.GetStream();
        async Task<byte[]> Read(int count)
        {
            var buffer = new byte[count];
            await stream.ReadExactlyAsync(buffer, timeout.Token);
            return buffer;
        }
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, timeout.Token);
        if ((await Read(2))[1] != 0) throw new IOException("SOCKS auth");
        await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(targetPort >> 8), (byte)targetPort }, timeout.Token);
        var reply = await Read(10);
        if (reply[1] != 0) throw new IOException("SOCKS connect " + reply[1]);
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nHost: 127.0.0.1\r\n\r\n"), timeout.Token);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(timeout.Token);
    }
}
