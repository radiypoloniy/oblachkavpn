using System.Net;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private const string realityLink = "vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=reality&sni=www.example.com&fp=chrome&pbk=SbVKOEMjK0sIlbwg4akyBg5mL5KZwwB-ed4eEE7YnRc&sid=6ba85179e30d4fc2&flow=xtls-rprx-vision&type=tcp#R";

    private static async Task DirectOutboundChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var peers = new[] { IPAddress.Parse("203.0.113.7") };
        await check("Xray VLESS REALITY Vision, Trojan ws and Shadowsocks 2022 translate to sing-box outbounds the service accepts unchanged", async () =>
        {
            var reality = DirectOutbound.From(Profiles.Parse(realityLink))!;
            Assert(reality["type"]!.GetValue<string>() == "vless" && reality["flow"]!.GetValue<string>() == "xtls-rprx-vision" && reality["packet_encoding"]!.GetValue<string>() == "xudp");
            Assert(reality["tls"]!["reality"]!["public_key"]!.GetValue<string>() == "SbVKOEMjK0sIlbwg4akyBg5mL5KZwwB-ed4eEE7YnRc" && reality["tls"]!["utls"]!["fingerprint"]!.GetValue<string>() == "chrome");
            var trojan = DirectOutbound.From(Profiles.Parse("trojan://fixture-secret@203.0.113.7:8443?type=ws&path=%2Fws&host=cdn.example.com&sni=example.com#T"))!;
            Assert(trojan["transport"]!["type"]!.GetValue<string>() == "ws" && trojan["transport"]!["headers"]!["Host"]!.GetValue<string>() == "cdn.example.com");
            var ss = DirectOutbound.From(Profiles.Parse("ss://" + B64("2022-blake3-aes-128-gcm:ISQz02o+aQdntLC3EX4AjQ==").TrimEnd('=') + "@203.0.113.7:443#S"))!;
            Assert(ss["method"]!.GetValue<string>() == "2022-blake3-aes-128-gcm");
            foreach (var outbound in new[] { reality, trojan, ss })
            {
                var accepted = DirectOutboundPolicy.Validate(outbound, peers);
                Assert(JsonNode.DeepEquals(accepted, outbound));
                // Закреплённая версия sing-box принимает выход в рабочей конфигурации.
                var config = new JsonObject { ["log"] = new JsonObject { ["disabled"] = true }, ["outbounds"] = new JsonArray(accepted.DeepClone()) };
                await CoreConfigurationCheck.ValidateAsync(new(CoreKind.SingBox, Path.Combine(AppContext.BaseDirectory, "tools", "sing-box.exe")), config, CancellationToken.None);
            }
        });
        await check("Profiles sing-box cannot carry the same way stay on the window core", () => Sync(() =>
        {
            Assert(DirectOutbound.From(Profiles.Parse("vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=reality&sni=www.example.com&pbk=SbVKOEMjK0sIlbwg4akyBg5mL5KZwwB-ed4eEE7YnRc&type=xhttp&path=%2Fx#X")) is null);
            Assert(DirectOutbound.From(Profiles.Parse("vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=tls&sni=example.com&type=ws&path=%2Fws%3Fed%3D2048#E")) is null);
            Assert(DirectOutbound.From(Profiles.Parse("vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=tls&sni=example.com&ech=AEX%2B#ECH")) is null);
            var mux = Profiles.Parse(realityLink); mux.Outbound["mux"] = new JsonObject { ["enabled"] = true };
            Assert(DirectOutbound.From(mux) is null);
            var sockopt = Profiles.Parse(realityLink); sockopt.Outbound["streamSettings"]!["sockopt"] = new JsonObject { ["dialerProxy"] = "x" };
            Assert(DirectOutbound.From(sockopt) is null);
            // Адрес не IP — профиль ещё не закреплён планом узла.
            Assert(DirectOutbound.From(Profiles.Parse(realityLink.Replace("203.0.113.7", "node.example.com"))) is null);
        }));
        await check("Lease carries direct outbounds only for its own servers; tunnel core gets a server selector and sing-box accepts it", async () =>
        {
            var outbound = DirectOutbound.From(Profiles.Parse(realityLink))!;
            var stats = CoreStatsEntry.Create(23999);
            var request = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["203.0.113.7", "198.51.100.9"], Stats: stats, Direct: [outbound, null]);
            var lease = NetworkHelperContracts.Validate(request);
            Assert(lease.Direct!.Count == 2 && lease.Direct[1] is null && !lease.HasDirect);
            // Служба строит конфигурацию из запроса, собранного заново по аренде: выходы не теряются.
            Assert(SingBoxTunnelProcess.RequestFor(lease).Direct is { Length: 2 } again && again[0] is not null && again[1] is null);
            // И бэкенд службы, который переносит поля аренды в ядро по одному (так в 0.10 терялся вход «напрямую»).
            ValidatedNetworkLease? started = null; var up = false;
            using (var tunnel = new WindowsTunnelBackend((l, _) => { started = l; up = true; return new FakeTunnelProcess(); }, _ => up,
                delay: static (_, _) => Task.CompletedTask))
            {
                var change = tunnel.Prepare(lease).Single();
                tunnel.Apply(lease.LeaseId, change);
            }
            Assert(started?.Direct is { Count: 2 } carried && carried[0] is not null);
            ExpectUserError(() => NetworkHelperContracts.Validate(request with { Stats = null }));
            ExpectUserError(() => NetworkHelperContracts.Validate(request with { Direct = [null, null] }));
            ExpectUserError(() => NetworkHelperContracts.Validate(request with { PeerAddresses = ["198.51.100.9"] }));
            ExpectUserError(() => NetworkHelperContracts.Validate(new(1, Guid.NewGuid(), NetworkLeaseAction.Release, 0, [], Direct: [outbound])));
            await CoreConfigurationCheck.ValidateTunAsync(request, CancellationToken.None);
            var config = SingBoxTunConfig.Create(request);
            var selector = config["outbounds"]!.AsArray().Single(o => o!["type"]!.GetValue<string>() == "selector")!;
            Assert(selector["tag"]!.GetValue<string>() == SingBoxTunConfig.ServerSelector && selector["default"]!.GetValue<string>() == "direct-0");
            Assert(selector["outbounds"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(["direct-0", "local-core"]));
            Assert(config["route"]!["final"]!.GetValue<string>() == SingBoxTunConfig.ServerSelector && config["dns"]!["servers"]![0]!["detour"]!.GetValue<string>() == SingBoxTunConfig.ServerSelector);
            // Без выходов — всё как раньше: туннель смотрит в ядро окна.
            Assert(SingBoxTunConfig.Create(request with { Direct = null })["route"]!["final"]!.GetValue<string>() == "local-core");
        });
        await check("System session sends direct outbounds only while the window allows them", async () =>
        {
            var ssLink = "ss://" + B64("2022-blake3-aes-128-gcm:ISQz02o+aQdntLC3EX4AjQ==").TrimEnd('=') + "@203.0.113.7:443#S";
            foreach (var (allowed, selective) in new[] { (true, false), (false, false), (true, true) })
            {
                var calls = new List<string>(); var helper = new SessionHelper(calls); var core = new SessionCore(calls);
                await using var session = new SystemTunnelSession(core, helper) { DirectAllowed = () => allowed };
                var plan = await TunnelEndpoints.PrepareAsync(Profiles.Parse(ssLink), CancellationToken.None);
                await session.StartWithSparesAsync([plan], CancellationToken.None, directByDefault: selective);
                Assert(allowed ? helper.LastRequest!.Direct is [{ }] && session.DirectActive : helper.LastRequest!.Direct is null && !session.DirectActive);
                // Ядро окна не запускается, когда трафик идёт прямо из туннеля; в выборочном режиме — запускается.
                Assert(ReferenceEquals(core.CarriedBy, allowed && !selective ? session.Stats : null));
            }
        });
        await check("Window switches the tunnel core's server selector through the pinned sing-box clash API", async () =>
        {
            // Настоящее ядро без входов и без TUN: только переключатель и интерфейс на 127.0.0.1.
            var stats = CoreStatsEntry.Create(Engine.FreePort());
            var config = new JsonObject
            {
                ["log"] = new JsonObject { ["disabled"] = true },
                ["outbounds"] = new JsonArray(
                    new JsonObject { ["type"] = "direct", ["tag"] = SingBoxTunConfig.DirectTag(0) },
                    new JsonObject { ["type"] = "direct", ["tag"] = "local-core" },
                    new JsonObject { ["type"] = "selector", ["tag"] = SingBoxTunConfig.ServerSelector, ["outbounds"] = new JsonArray(SingBoxTunConfig.DirectTag(0), "local-core"), ["default"] = SingBoxTunConfig.DirectTag(0) }),
                ["experimental"] = new JsonObject { ["clash_api"] = new JsonObject { ["external_controller"] = "127.0.0.1:" + stats.Port, ["secret"] = stats.Secret } }
            };
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "tools", "sing-box.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "run", "-c", "stdin" }) start.ArgumentList.Add(a);
            using var process = System.Diagnostics.Process.Start(start)!;
            using var job = new ProcessJob(); job.Attach(process);
            try
            {
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                await process.StandardInput.WriteAsync(config.ToJsonString()); process.StandardInput.Close();
                var selected = false;
                for (var i = 0; i < 50 && !selected; i++) { await Task.Delay(100); selected = await CoreSelector.SelectAsync(stats, "local-core", CancellationToken.None); }
                Assert(selected);
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{stats.Port}/proxies/{SingBoxTunConfig.ServerSelector}");
                request.Headers.Authorization = new("Bearer", stats.Secret);
                var now = JsonNode.Parse(await (await http.SendAsync(request)).Content.ReadAsStringAsync())!["now"]!.GetValue<string>();
                Assert(now == "local-core");
                // Ядро окна в режиме туннеля: процесса нет, порт закреплён. Сама проверка связи
                // (/delay) идёт в интернет — она проверяется на стенде, а не здесь: sing-box подменяет
                // адрес http:// на gstatic, а выход тестового ядра на машине разработчика закрыт его VPN.
                using (var carried = new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), Path.GetTempPath()) { CarriedBy = stats })
                {
                    await carried.StartAsync(Profiles.Parse(realityLink), CancellationToken.None);
                    Assert(carried.State == ConnectionState.Running && carried.ProcessId == Environment.ProcessId && carried.Port > 0);
                    carried.Stop();
                    Assert(carried.ProcessId is null && carried.State == ConnectionState.Idle);
                }
                // Неответ ядра туннеля — «не прошло», а не исключение.
                Assert(await CoreSelector.DelayAsync(stats with { Secret = new string('0', 32) }, new Uri("https://www.cloudflare.com/"), CancellationToken.None) is null);
                // Чужой секрет не переключает.
                Assert(!await CoreSelector.SelectAsync(stats with { Secret = new string('0', 32) }, SingBoxTunConfig.DirectTag(0), CancellationToken.None));
            }
            finally { try { process.Kill(); } catch (InvalidOperationException) { } }
        });
        await check("Service rebuilds direct outbounds and refuses foreign servers, extra fields and disabled certificate checks", () => Sync(() =>
        {
            var good = DirectOutbound.From(Profiles.Parse(realityLink))!;
            JsonObject With(Action<JsonObject> change) { var copy = (JsonObject)good.DeepClone(); change(copy); return copy; }
            ExpectUserError(() => DirectOutboundPolicy.Validate(good, new[] { IPAddress.Parse("198.51.100.1") }));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["detour"] = "direct"), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["bind_interface"] = "Ethernet"), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["type"] = "direct"), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["tls"]!["insecure"] = true), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["tls"]!["certificate_path"] = "C:\\x.pem"), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["tls"]!.AsObject().Remove("utls")), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["server"] = "localhost"), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["flow"] = "xtls-rprx-direct"), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["transport"] = new JsonObject { ["type"] = "ws", ["path"] = "/a b" }), peers));
            ExpectUserError(() => DirectOutboundPolicy.Validate(With(o => o["tls"]!["enabled"] = "true"), peers));
        }));
    }
}
