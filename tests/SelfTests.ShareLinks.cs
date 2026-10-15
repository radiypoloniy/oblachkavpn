using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private const string trojanLink = "trojan://fixture-secret@example.com:8443?type=ws&path=%2Fws&host=cdn.example.com&sni=example.com#Trojan%20WS";
    private const string hy2Link = "hy2://fixture-pass@example.com:443/?sni=example.com&obfs=salamander&obfs-password=fixture-obfs&mport=20000-50000#HY2";

    private static async Task ShareLinkChecksAsync(Func<string, Func<Task>, Task> CheckAsync, string root)
    {
        await CheckAsync("Trojan link maps to Xray trojan outbound with TLS by default", () => Sync(() =>
        {
            var p = Profiles.Parse(trojanLink);
            Assert(p.Core == CoreKind.Xray && p.Name == "Trojan WS" && p.Transport == "TROJAN · WS / TLS");
            Assert(p.Outbound["protocol"]!.GetValue<string>() == "trojan");
            Assert(p.Outbound["settings"]!["servers"]![0]!["password"]!.GetValue<string>() == "fixture-secret");
            Assert(p.Outbound["streamSettings"]!["tlsSettings"]!["serverName"]!.GetValue<string>() == "example.com");
            Assert(p.Outbound["streamSettings"]!["wsSettings"]!["headers"]!["Host"]!.GetValue<string>() == "cdn.example.com");
            Assert(Profiles.XrayEndpoint(p.Outbound)["port"]!.GetValue<int>() == 8443);
        }));
        await CheckAsync("Trojan without TLS and without password is rejected", () => Sync(() =>
        {
            ExpectUserError(() => Profiles.Parse(trojanLink.Replace("type=ws", "type=ws&security=none")));
            ExpectUserError(() => Profiles.Parse("trojan://@example.com:443?security=tls#x"));
        }));
        await CheckAsync("Trojan JSON outbound round-trips through saved state", () => Sync(() =>
        {
            var p = Profiles.Parse(trojanLink);
            var again = Profiles.Parse(p.Outbound.ToJsonString());
            Assert(Profiles.Fingerprint(again) == Profiles.Fingerprint(p));
        }));
        await CheckAsync("Hysteria2 link maps to sing-box with salamander and port hopping", () => Sync(() =>
        {
            var p = Profiles.Parse(hy2Link);
            Assert(p.Core == CoreKind.SingBox && p.Name == "HY2");
            Assert(p.Outbound["type"]!.GetValue<string>() == "hysteria2" && p.Outbound["password"]!.GetValue<string>() == "fixture-pass");
            Assert(p.Outbound["obfs"]!["type"]!.GetValue<string>() == "salamander");
            Assert(p.Outbound["server_ports"]![0]!.GetValue<string>() == "20000:50000");
            Assert(p.Outbound["tls"]!["server_name"]!.GetValue<string>() == "example.com");
            Assert(Profiles.Parse(hy2Link.Replace("hy2://", "hysteria2://")).Core == CoreKind.SingBox);
        }));
        await CheckAsync("Hysteria2 insecure, pinning and bad port ranges are rejected", () => Sync(() =>
        {
            ExpectUserError(() => Profiles.Parse(hy2Link.Replace("#HY2", "&insecure=1#HY2")));
            ExpectUserError(() => Profiles.Parse(hy2Link.Replace("#HY2", "&pinSHA256=AA#HY2")));
            ExpectUserError(() => Profiles.Parse(hy2Link.Replace("20000-50000", "50000-20000")));
            ExpectUserError(() => Profiles.Parse(hy2Link.Replace("obfs=salamander", "obfs=other")));
        }));
        await CheckAsync("Unknown link parameters are named without leaking values", () => Sync(() =>
        {
            try { Profiles.Parse(link.Replace("#Test", "&newparam=secret-value-123#Test")); throw new InvalidOperationException("accepted"); }
            catch (UserError ex) { Assert(ex.Message.Contains("newparam") && !ex.Message.Contains("secret-value-123")); }
        }));
        await CheckAsync("REALITY pqv and TLS ech map to Xray fields", () => Sync(() =>
        {
            var reality = Profiles.Parse("vless://11111111-1111-4111-8111-111111111111@example.com:443?security=reality&sni=example.org&pbk=fixturekey&pqv=fixturepqv#R");
            Assert(reality.Outbound["streamSettings"]!["realitySettings"]!["mldsa65Verify"]!.GetValue<string>() == "fixturepqv");
            var ech = Profiles.Parse(link.Replace("#Test", "&ech=fixtureech#Test"));
            Assert(ech.Outbound["streamSettings"]!["tlsSettings"]!["echConfigList"]!.GetValue<string>() == "fixtureech");
        }));
        await CheckAsync("Mixed subscription imports VLESS, Trojan and Hysteria2 and reports skip reasons", () => Sync(() =>
        {
            var source = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", link, trojanLink, hy2Link,
                "ss://fixture@example.com:443", trojanLink.Replace("type=ws", "type=ws&security=none"))));
            var result = Subscriptions.Parse(source);
            Assert(result.Profiles.Count == 3 && result.Unsupported == 1 && result.Invalid == 1 && result.Reasons.Count == 1);
            Assert(result.Profiles.Select(p => p.Core).Distinct().Count() == 2);
        }));
        await CheckAsync("Empty result explains the first rejection reason", () => Sync(() =>
        {
            try { Subscriptions.Parse(trojanLink.Replace("type=ws", "type=ws&security=none")); throw new InvalidOperationException("accepted"); }
            catch (UserError ex) { Assert(ex.Message.Contains("Причина")); }
        }));
        await CheckAsync("TUN endpoint pinning supports Trojan servers", async () =>
        {
            var plan = await TunnelEndpoints.PrepareAsync(Profiles.Parse(trojanLink), CancellationToken.None,
                (_, _) => Task.FromResult(new[] { IPAddress.Parse("192.0.2.10") }));
            Assert(Profiles.XrayEndpoint(plan.PinnedProfile.Outbound)["address"]!.GetValue<string>() == "192.0.2.10");
            Assert(plan.PinnedProfile.Outbound["streamSettings"]!["tlsSettings"]!["serverName"]!.GetValue<string>() == "example.com");
        });
        await CheckAsync("Pinned cores accept Trojan and Hysteria2 runtime configs", async () =>
        {
            var tools = Path.Combine(AppContext.BaseDirectory, "tools");
            await CoreConfigurationCheck.ValidateAsync(new(CoreKind.Xray, Path.Combine(tools, "xray.exe")),
                Profiles.RuntimeConfig(Profiles.Parse(trojanLink).Outbound, 34570), CancellationToken.None);
            var singBox = new CoreAdapter(CoreKind.SingBox, Path.Combine(tools, "sing-box.exe"));
            await CoreConfigurationCheck.ValidateAsync(singBox, singBox.RuntimeConfig(Profiles.Parse(hy2Link).Outbound, 34571), CancellationToken.None);
        });
    }
}
