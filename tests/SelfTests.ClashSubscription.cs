namespace Oblachka;

public static partial class SelfTests
{
    private const string ClashFixture = """
        mixed-port: 7890
        # Подписка Mihomo
        proxies:
          - name: "🇩🇪 Германия"
            type: vless
            server: de.example.com
            port: 443
            uuid: 11111111-1111-4111-8111-111111111111
            network: tcp
            tls: true
            udp: true
            flow: xtls-rprx-vision
            servername: www.example.org
            client-fingerprint: chrome
            reality-opts:
              public-key: fixturekey
              short-id: ab12
          - name: 'WS # не комментарий'
            type: vless
            server: ws.example.com
            port: 443
            uuid: 22222222-2222-4222-8222-222222222222
            network: ws
            tls: true
            ws-opts:
              path: /ws
              headers:
                Host: cdn.example.com
          - {name: Trojan gRPC, type: trojan, server: tr.example.com, port: 8443, password: "p@ss word", sni: tr.example.com, network: grpc, grpc-opts: {grpc-service-name: svc}}
          - name: HY2
            type: hysteria2
            server: hy.example.com
            port: 443
            password: secret
            sni: hy.example.com
            obfs: salamander
            obfs-password: fixture-obfs
          - name: XHTTP
            type: vless
            server: x.example.com
            port: 443
            uuid: 33333333-3333-4333-8333-333333333333
            network: xhttp
            tls: true
            servername: x.example.com
            alpn: [h2, http/1.1]
            xhttp-opts:
              path: /sync/
              mode: packet-up
              x-padding-bytes: 96-1040
              x-padding-obfs-mode: true
              uplink-chunk-size: 4096
              reuse-settings:
                max-connections: '8'
                c-max-reuse-times: 36-96
          - name: SS
            type: ss
            server: ss.example.com
            port: 8388
            cipher: aes-256-gcm
            password: x
          - name: Insecure
            type: trojan
            server: bad.example.com
            port: 443
            password: x
            skip-cert-verify: true
        proxy-groups:
          - name: Auto
            type: url-test
            proxies: [Германия]
        """;

    private static async Task ClashSubscriptionChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Clash/Mihomo subscriptions: proxies become VLESS/Trojan/Hysteria2 profiles with the usual checks", () => Sync(() =>
        {
            var result = Subscriptions.Parse(ClashFixture);
            var names = result.Profiles.Select(p => p.Name).ToArray();
            if (!names.SequenceEqual(new[] { "🇩🇪 Германия", "WS # не комментарий", "Trojan gRPC", "HY2", "XHTTP" }))
                throw new InvalidOperationException("Clash names: " + string.Join(" | ", names));
            Assert(result.Unsupported == 1 && result.Invalid == 1);
            var reality = result.Profiles[0].Outbound;
            Assert(reality["streamSettings"]!["security"]!.GetValue<string>() == "reality" &&
                   reality["streamSettings"]!["realitySettings"]!["publicKey"]!.GetValue<string>() == "fixturekey" &&
                   reality["settings"]!["vnext"]![0]!["users"]![0]!["flow"]!.GetValue<string>() == "xtls-rprx-vision");
            var ws = result.Profiles[1].Outbound["streamSettings"]!;
            Assert(ws["network"]!.GetValue<string>() == "ws" && ws["wsSettings"]!["headers"]!["Host"]!.GetValue<string>() == "cdn.example.com");
            var trojan = result.Profiles[2].Outbound;
            Assert(trojan["settings"]!["servers"]![0]!["password"]!.GetValue<string>() == "p@ss word" &&
                   trojan["streamSettings"]!["grpcSettings"]!["serviceName"]!.GetValue<string>() == "svc");
            Assert(result.Profiles[3].Core == CoreKind.SingBox && result.Profiles[3].Outbound["obfs"]!["password"]!.GetValue<string>() == "fixture-obfs");
            // Тонкие настройки XHTTP: имена Mihomo → extra Xray, числа — числами, диапазоны — строками.
            var extra = result.Profiles[4].Outbound["streamSettings"]!["xhttpSettings"]!["extra"]!;
            Assert(extra["xPaddingBytes"]!.GetValue<string>() == "96-1040" && extra["xPaddingObfsMode"]!.GetValue<bool>() &&
                   extra["uplinkChunkSize"]!.GetValue<long>() == 4096 && extra["xmux"]!["maxConnections"]!.GetValue<long>() == 8 &&
                   extra["xmux"]!["cMaxReuseTimes"]!.GetValue<string>() == "36-96");
            ExpectUserError(() => Subscriptions.Parse("proxies:\n  - {name: only-ss, type: ss, server: a.example.com, port: 1, cipher: x, password: y}\n"));
        }));
        await check("sing-box subscriptions name nodes by their tag", () => Sync(() =>
        {
            const string json = """
                {"outbounds":[
                  {"type":"vless","tag":"🇫🇮 Финляндия","server":"fi.example.com","server_port":443,"uuid":"11111111-1111-4111-8111-111111111111","flow":"xtls-rprx-vision",
                   "tls":{"enabled":true,"server_name":"www.example.org","utls":{"enabled":true,"fingerprint":"chrome"},"reality":{"enabled":true,"public_key":"fixturekey","short_id":"ab12"}}},
                  {"type":"selector","tag":"proxy","outbounds":["🇫🇮 Финляндия"]},
                  {"type":"direct","tag":"direct"}]}
                """;
            var result = Subscriptions.Parse(json);
            if (result.Profiles.Count != 1 || result.Profiles[0].Name != "🇫🇮 Финляндия")
                throw new InvalidOperationException("sing-box names: " + string.Join(" | ", result.Profiles.Select(p => p.Name)));
        }));
    }
}
