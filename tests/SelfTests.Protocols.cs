using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // Ключи WireGuard из документации sing-box: настоящие 32 байта, ни к чему не подходят.
    private const string WgPrivate = "YNXtAzepDqRv9H52osJVDQnznT5AL11eh6uuLy7ERWE=", WgPublic = "Z1XXLsKYkYxuiYjJIkRvtIKFepCYHTgON+GwPq7SOV4=";
    private const string Uuid = "11111111-1111-4111-8111-111111111111";

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static string VmessLink(string json) => "vmess://" + B64(json);
    private static string SsLink => "ss://" + B64("chacha20-ietf-poly1305:fixture-pass").TrimEnd('=') + "@203.0.113.20:8388#SS";
    private static string WgLink => "wireguard://" + Uri.EscapeDataString(WgPrivate) + "@203.0.113.24:51820?publickey=" + Uri.EscapeDataString(WgPublic) +
        "&address=10.0.0.2%2F32&mtu=1420&reserved=1,2,3#WG";
    private static string VmessWs => VmessLink("{\"v\":\"2\",\"ps\":\"VM\",\"add\":\"203.0.113.21\",\"port\":\"443\",\"id\":\"" + Uuid +
        "\",\"aid\":\"0\",\"scy\":\"auto\",\"net\":\"ws\",\"host\":\"cdn.example.org\",\"path\":\"/ws\",\"tls\":\"tls\",\"sni\":\"example.org\",\"fp\":\"chrome\"}");
    private const string TuicLink = "tuic://" + Uuid + ":fixture-pass@203.0.113.22:443?congestion_control=bbr&udp_relay_mode=native&alpn=h3&sni=example.org#TUIC";
    private const string AnyTlsLink = "anytls://fixture-pass@203.0.113.23:443/?sni=example.org&fp=chrome#ANY";

    private static async Task ProtocolChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Protocols: ss, vmess, tuic, anytls and wireguard links become sing-box profiles with their own labels", () => Sync(() =>
        {
            var ss = Profiles.Parse(SsLink);
            Assert(ss.Core == CoreKind.SingBox && ss.Name == "SS" && ss.Transport == "SHADOWSOCKS · CHACHA20-IETF-POLY1305");
            Assert(ss.Outbound["password"]!.GetValue<string>() == "fixture-pass" && ss.Outbound["server_port"]!.GetValue<int>() == 8388);
            // Shadowsocks 2022 без base64, Outline с ?outline=1 и старый вид base64(всё).
            var ss2022 = Profiles.Parse("ss://2022-blake3-aes-128-gcm:" + Uri.EscapeDataString("AAAAAAAAAAAAAAAAAAAAAA==") + "@203.0.113.20:8443/?outline=1#S22");
            Assert(ss2022.Outbound["method"]!.GetValue<string>() == "2022-blake3-aes-128-gcm" && ss2022.Outbound["password"]!.GetValue<string>() == "AAAAAAAAAAAAAAAAAAAAAA==");
            Assert(Profiles.Parse("ss://" + B64("aes-256-gcm:p@ss@203.0.113.20:8388") + "#Old").Outbound["password"]!.GetValue<string>() == "p@ss");
            var vm = Profiles.Parse(VmessWs);
            Assert(vm.Name == "VM" && vm.Transport == "VMESS · WS / TLS" && vm.Outbound["transport"]!["headers"]!["Host"]!.GetValue<string>() == "cdn.example.org");
            Assert(vm.Outbound["tls"]!["server_name"]!.GetValue<string>() == "example.org" && vm.Outbound["tls"]!["utls"]!["fingerprint"]!.GetValue<string>() == "chrome");
            var tuic = Profiles.Parse(TuicLink);
            Assert(tuic.Transport == "TUIC / QUIC / TLS" && tuic.Outbound["congestion_control"]!.GetValue<string>() == "bbr");
            Assert(Profiles.Parse(AnyTlsLink).Transport == "ANYTLS / TLS");
            var wg = Profiles.Parse(WgLink);
            Assert(wg.Transport == "WIREGUARD / UDP" && wg.Outbound["peers"]![0]!["reserved"]!.AsArray().Count == 3 && wg.Outbound["mtu"]!.GetValue<int>() == 1420);
        }));
        await check("Protocols: weak ciphers, plugins, legacy VMess and disabled certificate checks are refused", () => Sync(() =>
        {
            ExpectUserError(() => Profiles.Parse("ss://" + B64("rc4-md5:x") + "@203.0.113.20:8388#Old"));
            ExpectUserError(() => Profiles.Parse("ss://" + B64("none:x") + "@203.0.113.20:8388#None"));
            ExpectUserError(() => Profiles.Parse(SsLink.Replace("#SS", "?plugin=obfs-local%3Bobfs%3Dhttp#SS")));
            ExpectUserError(() => Profiles.Parse(VmessLink("{\"add\":\"203.0.113.21\",\"port\":\"443\",\"id\":\"" + Uuid + "\",\"scy\":\"none\"}")));
            ExpectUserError(() => Profiles.Parse(VmessLink("{\"add\":\"203.0.113.21\",\"port\":\"443\",\"id\":\"" + Uuid + "\",\"aid\":\"64\"}")));
            ExpectUserError(() => Profiles.Parse(VmessLink("{\"add\":\"203.0.113.21\",\"port\":\"443\",\"id\":\"" + Uuid + "\",\"tls\":\"tls\",\"allowInsecure\":true}")));
            ExpectUserError(() => Profiles.Parse(TuicLink.Replace("#TUIC", "&allow_insecure=1#TUIC")));
            ExpectUserError(() => Profiles.Parse(AnyTlsLink.Replace("#ANY", "&insecure=1#ANY")));
            ExpectUserError(() => Profiles.Parse(WgLink.Replace(Uri.EscapeDataString(WgPublic), "short")));
            // TLS обязателен там, где протокол сам не шифрует.
            ExpectUserError(() => SingBoxProfiles.Parse(new JsonObject { ["type"] = "trojan", ["server"] = "203.0.113.1", ["server_port"] = 443, ["password"] = "x" }, "T"));
        }));
        await check("Protocols: WireGuard .conf imports as one connection; AmneziaWG goes to the tunnel core; multi-peer files are explained", async () =>
        {
            var conf = "[Interface]\nPrivateKey = " + WgPrivate + "\nAddress = 10.0.0.2/32, fd00::2/128\nDNS = 1.1.1.1\nMTU = 1380\n\n[Peer]\nPublicKey = " + WgPublic +
                "\nAllowedIPs = 0.0.0.0/0, ::/0\nEndpoint = vpn.example.org:51820\nPersistentKeepalive = 25\n";
            var result = Subscriptions.Parse(conf);
            var wg = result.Profiles.Single();
            Assert(wg.Name == "WireGuard · vpn.example.org" && wg.Outbound["address"]!.AsArray().Count == 2 && wg.Outbound["mtu"]!.GetValue<int>() == 1380);
            Assert(wg.Outbound["peers"]![0]!["persistent_keepalive_interval"]!.GetValue<int>() == 25 && ProfileServer.Of(wg) == ("vpn.example.org", 51820));
            ExpectUserError(() => Subscriptions.Parse(conf + "\n[Peer]\nPublicKey = " + WgPublic + "\nEndpoint = b.example.org:51820\n"));
            // AmneziaWG (2.0: S3, S4, диапазоны H, сигнатура I1) — выход amneziawg своего ядра туннеля.
            var awgConf = conf.Replace("MTU = 1380", "MTU = 1380\nJc = 4\nJmin = 10\nJmax = 50\nS1 = 54\nS2 = 125\nS3 = 9\nS4 = 3\nH1 = 1206245290-1206245390\nH2 = 2083540914\nH3 = 2121082093\nH4 = 1468224581\nI1 = <b 0x1a2b><r 16>");
            var awg = Subscriptions.Parse(awgConf).Profiles.Single();
            Assert(awg.Name == "AmneziaWG · vpn.example.org" && awg.Transport == "AMNEZIAWG / UDP" && ProfileServer.IsAmnezia(awg) && ProfileServer.IsUdp(awg));
            Assert(ProfileServer.Of(awg) == ("vpn.example.org", 51820) && CoreAdapter.FileFor(awg) == TunCore.FileName && CoreAdapter.FileFor(wg) == "sing-box.exe");
            Assert(awg.Outbound["obfuscation"]!["h1"]!.GetValue<string>() == "1206245290-1206245390" && awg.Outbound["obfuscation"]!["i1"]!.GetValue<string>() == "<b 0x1a2b><r 16>");
            // Настоящее ядро туннеля принимает собранную конфигурацию: имена полей совпадают с core/amneziawg.go.
            var core = new CoreAdapter(CoreKind.SingBox, Path.Combine(AppContext.BaseDirectory, "tools", CoreAdapter.FileFor(awg)));
            var runtime = core.RuntimeConfig(awg.Outbound, 10808);
            Assert(runtime["outbounds"]!.AsArray().Single()!["type"]!.GetValue<string>() == "amneziawg");
            await CoreConfigurationCheck.ValidateAsync(core, runtime, CancellationToken.None);
            // Лишняя строка в значении, «=» в сигнатуре, нецифровой H, параметры в [Peer] — отказ.
            ExpectUserError(() => Subscriptions.Parse(awgConf.Replace("H2 = 2083540914", "H2 = 1;2")));
            ExpectUserError(() => Subscriptions.Parse(awgConf.Replace("I1 = <b 0x1a2b><r 16>", "I1 = <b 0x1a>=x")));
            ExpectUserError(() => Subscriptions.Parse(awgConf.Replace("PersistentKeepalive = 25", "PersistentKeepalive = 25\nJc = 4")));
        });
        await check("Protocols: Xray JSON with VMess and Shadowsocks, sing-box config with a WireGuard endpoint", () => Sync(() =>
        {
            var vmess = Profiles.Parse("{\"outbounds\":[{\"protocol\":\"vmess\",\"settings\":{\"vnext\":[{\"address\":\"203.0.113.21\",\"port\":443,\"users\":[{\"id\":\"" + Uuid +
                "\",\"alterId\":0,\"security\":\"auto\"}]}]},\"streamSettings\":{\"network\":\"ws\",\"security\":\"tls\",\"tlsSettings\":{\"serverName\":\"example.org\"}}},{\"protocol\":\"freedom\"}]}");
            Assert(vmess.Core == CoreKind.Xray && vmess.Transport == "VMESS · WS / TLS" && ProfileServer.Of(vmess) == ("203.0.113.21", 443));
            var ss = Profiles.Parse("{\"protocol\":\"shadowsocks\",\"settings\":{\"servers\":[{\"address\":\"203.0.113.20\",\"port\":8388,\"method\":\"2022-blake3-aes-128-gcm\",\"password\":\"AAAAAAAAAAAAAAAAAAAAAA==\"}]}}");
            Assert(ss.Transport == "SHADOWSOCKS · TCP" && ProfileServer.Of(ss) == ("203.0.113.20", 8388));
            ExpectUserError(() => Profiles.Parse("{\"protocol\":\"shadowsocks\",\"settings\":{\"servers\":[{\"address\":\"203.0.113.20\",\"port\":8388,\"method\":\"rc4-md5\",\"password\":\"x\"}]}}"));
            var config = new JsonObject { ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
                ["endpoints"] = new JsonArray(Profiles.Parse(WgLink).Outbound.DeepClone()) };
            Assert(Profiles.Parse(config.ToJsonString()).Transport == "WIREGUARD / UDP");
        }));
        await check("Protocols: Clash ss, vmess, tuic, anytls and wireguard proxies are imported; plugins and AmneziaWG are skipped", () => Sync(() =>
        {
            var yaml = "proxies:\n" +
                "  - {name: SS, type: ss, server: 203.0.113.20, port: 8388, cipher: aes-128-gcm, password: fixture-pass}\n" +
                "  - {name: SSP, type: ss, server: 203.0.113.20, port: 8389, cipher: aes-128-gcm, password: x, plugin: obfs}\n" +
                "  - name: VM\n    type: vmess\n    server: 203.0.113.21\n    port: 443\n    uuid: " + Uuid + "\n    alterId: 0\n    cipher: auto\n    tls: true\n    servername: example.org\n    network: ws\n    ws-opts:\n      path: /ws\n      headers:\n        Host: cdn.example.org\n" +
                "  - {name: TU, type: tuic, server: 203.0.113.22, port: 443, uuid: " + Uuid + ", password: p, alpn: [h3], congestion-controller: bbr}\n" +
                "  - {name: AT, type: anytls, server: 203.0.113.23, port: 443, password: p, sni: example.org}\n" +
                "  - {name: WG, type: wireguard, server: 203.0.113.24, port: 51820, ip: 10.0.0.2, private-key: \"" + WgPrivate + "\", public-key: \"" + WgPublic + "\", reserved: [1, 2, 3]}\n" +
                "  - {name: AWG, type: wireguard, server: 203.0.113.25, port: 51820, ip: 10.0.0.3, private-key: \"" + WgPrivate + "\", public-key: \"" + WgPublic + "\", amnezia-wg-option: {jc: 4}}\n";
            var result = Subscriptions.Parse(yaml);
            Assert(string.Join(",", result.Profiles.Select(p => p.Name)) == "SS,VM,TU,AT,WG" && result.Unsupported == 2);
            Assert(result.Profiles[1].Transport == "VMESS · WS / TLS" && result.Profiles[4].Outbound["address"]![0]!.GetValue<string>() == "10.0.0.2/32");
        }));
        await check("Protocols: server address for ping and tunnel works for every protocol; UDP ones are not TCP-pinged", async () =>
        {
            Assert(ServerPing.Endpoint(Profiles.Parse(SsLink)) == ("203.0.113.20", 8388) && ServerPing.Endpoint(Profiles.Parse(AnyTlsLink)) == ("203.0.113.23", 443));
            Assert(ServerPing.Endpoint(Profiles.Parse(TuicLink)) is null && ServerPing.Endpoint(Profiles.Parse(WgLink)) is null);
            var named = Profiles.Parse(WgLink.Replace("203.0.113.24", "wg.example.org"));
            var plan = await TunnelEndpoints.PrepareAsync(named, CancellationToken.None, (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.30") }));
            Assert(ProfileServer.Of(plan.PinnedProfile) == ("192.0.2.30", 51820) && plan.Peers.Single() == "192.0.2.30");
            var vm = await TunnelEndpoints.PrepareAsync(Profiles.Parse(VmessWs.Replace(B64("x"), "")), CancellationToken.None, (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.31") }));
            Assert(ProfileServer.Of(vm.PinnedProfile).Host == "203.0.113.21");
        });
        await check("Protocols: pinned Xray and sing-box accept runtime configs of every new protocol", async () =>
        {
            var tools = Path.Combine(AppContext.BaseDirectory, "tools");
            var singBox = new CoreAdapter(CoreKind.SingBox, Path.Combine(tools, "sing-box.exe"));
            var port = 34580;
            foreach (var link in new[] { SsLink, VmessWs, TuicLink, AnyTlsLink, WgLink })
                await CoreConfigurationCheck.ValidateAsync(singBox, singBox.RuntimeConfig(Profiles.Parse(link).Outbound, port++), CancellationToken.None);
            var xray = new CoreAdapter(CoreKind.Xray, Path.Combine(tools, "xray.exe"));
            var ss = Profiles.Parse("{\"protocol\":\"shadowsocks\",\"settings\":{\"servers\":[{\"address\":\"203.0.113.20\",\"port\":8388,\"method\":\"2022-blake3-aes-128-gcm\",\"password\":\"AAAAAAAAAAAAAAAAAAAAAA==\"}]}}");
            await CoreConfigurationCheck.ValidateAsync(xray, xray.RuntimeConfig(ss.Outbound, port++), CancellationToken.None);
        });
    }
}
