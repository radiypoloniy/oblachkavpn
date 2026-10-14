using System.Text;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // Заголовок, как его отдаёт панель сервиса (сокращён до сути).
    private const string HappRouting = """
        {"Name":"Облачка/рутинг","GlobalProxy":"true","RouteOrder":"block-direct-proxy",
         "Geositeurl":"https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat","Geoipurl":"http://insecure.invalid/geoip.dat",
         "DirectSites":["domain:.ru","domain:.xn--p1ai","geosite:category-ru","domain:bitrix24.com","bad entry with spaces","regexp:(?=x)"],
         "DirectIp":["10.0.0.0/8","255.255.255.255","geoip:private","300.1.1.1"],
         "ProxySites":[],"ProxyIp":[],"BlockSites":["full:ads.example.com"],"BlockIp":[],"UnknownField":1}
        """;

    private static async Task RoutingChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Routing header in Happ format parses into a validated profile; unsafe entries are dropped", () => Sync(() =>
        {
            var header = "happ://routing/add/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(HappRouting));
            var profile = RoutingProfile.ParseHapp(header) ?? throw new InvalidOperationException("Header not parsed");
            Assert(profile.Name == "Облачка/рутинг" && profile.GlobalProxy && profile.Order == "block-direct-proxy");
            Assert(profile.DirectSites.Contains("domain:ru") && profile.DirectSites.Contains("geosite:category-ru") && !profile.DirectSites.Any(s => s.StartsWith("regexp")));
            Assert(!profile.DirectSites.Any(s => s.Contains(' ')));
            Assert(profile.DirectIp.SequenceEqual(new[] { "10.0.0.0/8", "255.255.255.255/32", "geoip:private" }));
            Assert(profile.BlockSites.Single() == "full:ads.example.com");
            Assert(profile.GeositeUrl!.StartsWith("https://") && profile.GeoipUrl is null);
            Assert(RoutingProfile.ParseHapp("happ://routing/add/not-json") is null && RoutingProfile.ParseHapp("https://x/y") is null);
            Assert(profile.GeositeTags.Single() == "category-ru" && profile.GeoipTags.Single() == "private");
        }));

        await check("Geo databases: categories are read from geosite/geoip protobuf and expanded into lists", () => Sync(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "oblachka-geo-" + Guid.NewGuid().ToString("N"));
            try
            {
                var geo = new GeoStore(folder);
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(geo.PathOf(GeoStore.Kind.Geosite), GeositeFixture());
                File.WriteAllText(geo.PathOf(GeoStore.Kind.Geosite) + ".json", "{\"Kind\":0,\"Updated\":\"2026-10-14T00:00:00+00:00\",\"Bytes\":10,\"Source\":\"fixture\"}");
                File.WriteAllBytes(geo.PathOf(GeoStore.Kind.Geoip), GeoipFixture());
                File.WriteAllText(geo.PathOf(GeoStore.Kind.Geoip) + ".json", "{\"Kind\":1,\"Updated\":\"2026-10-14T00:00:00+00:00\",\"Bytes\":10,\"Source\":\"fixture\"}");
                Assert(GeoDat.LooksValid(GeositeFixture()) && !GeoDat.LooksValid(Encoding.UTF8.GetBytes("<html>not found</html>")));
                var header = "happ://routing/add/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(HappRouting));
                var routing = CompiledRouting.Compile(new[] { RoutingProfile.ParseHapp(header)!, RoutingProfile.RussiaDirect }, geo)!;
                Assert(routing.Missing.Length == 0);
                Assert(routing.Direct.Suffix.Contains("yandex.ru") && routing.Direct.Full.Contains("gosuslugi.ru") && routing.Direct.Suffix.Contains("su"));
                Assert(routing.Direct.Cidr.Contains("192.168.0.0/16") && routing.Direct.Cidr.Contains("10.0.0.0/8"));
                Assert(routing.Decide("music.yandex.ru") == SiteAction.Direct && routing.Decide("ads.example.com") == SiteAction.Block && routing.Decide("youtube.com") is null);
                Assert(routing.HasDirect);
                var noBases = CompiledRouting.Compile(new[] { RoutingProfile.RussiaDirect }, new GeoStore(Path.Combine(folder, "empty")))!;
                Assert(noBases.Missing.Single() == "geosite:category-ru" && noBases.Direct.Suffix.Contains("ru"));
            }
            finally { Directory.Delete(folder, true); }
        }));

        await check("Routing lists compile into Xray and sing-box configs that the real cores accept; TUN lets the core's direct traffic out", async () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "oblachka-geo-" + Guid.NewGuid().ToString("N"));
            try
            {
                var geo = new GeoStore(folder);
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(geo.PathOf(GeoStore.Kind.Geosite), GeositeFixture());
                File.WriteAllText(geo.PathOf(GeoStore.Kind.Geosite) + ".json", "{\"Kind\":0,\"Updated\":\"2026-10-14T00:00:00+00:00\",\"Bytes\":10,\"Source\":\"fixture\"}");
                var routing = CompiledRouting.Compile(new[] { RoutingProfile.RussiaDirect with { BlockSites = ["full:ads.example.com"] } }, geo)!;
                var tools = Path.Combine(AppContext.BaseDirectory, "tools");
                var xray = new CoreAdapter(CoreKind.Xray, Path.Combine(tools, "xray.exe"));
                var config = RoutingPlan.Compile(xray, Profiles.Parse(link), 10808, [new("example.org", SiteAction.Direct)], routing);
                Assert(config["inbounds"]![0]!["sniffing"]!["routeOnly"]!.GetValue<bool>());
                var rules = (JsonArray)config["routing"]!["rules"]!;
                // Правило пользователя первым, затем блок и «напрямую» из списков, последним — всё в туннель.
                Assert(rules[0]!["domain"]![0]!.GetValue<string>() == "domain:example.org");
                Assert(rules[1]!["outboundTag"]!.GetValue<string>() == "user-block" && rules[2]!["outboundTag"]!.GetValue<string>() == "user-direct");
                Assert(rules[^1]!["outboundTag"]!.GetValue<string>() == "tunnel");
                await CoreConfigurationCheck.ValidateAsync(xray, config, CancellationToken.None);
                var hy2 = Profiles.Parse(hy2Link);
                var singBox = new CoreAdapter(CoreKind.SingBox, Path.Combine(tools, "sing-box.exe"));
                var singConfig = RoutingPlan.Compile(singBox, hy2, 10808, [], routing with { GlobalProxy = false });
                Assert(singConfig["route"]!["rules"]![0]!["action"]!.GetValue<string>() == "sniff" && singConfig["route"]!["final"]!.GetValue<string>() == "user-direct");
                await CoreConfigurationCheck.ValidateAsync(singBox, singConfig, CancellationToken.None);

                var lease = NetworkAcquire() with { CoreDirect = true };
                var tun = SingBoxTunConfig.Create(lease);
                var tunRules = (JsonArray)tun["route"]!["rules"]!;
                Assert(tunRules[1]!["process_path"]!.AsArray().Count == 2 && tunRules[1]!["outbound"]!.GetValue<string>() == SingBoxTunConfig.UserDirect);
                Assert(NetworkHelperContracts.Validate(lease).HasDirect);
                await CoreConfigurationCheck.ValidateTunAsync(lease, CancellationToken.None);
                ExpectUserError(() => NetworkHelperContracts.Validate(new(1, Guid.NewGuid(), NetworkLeaseAction.Release, 0, [], CoreDirect: true)));
            }
            finally { Directory.Delete(folder, true); }
        });
    }

    // Минимальная запись protobuf для синтетических баз.
    private static byte[] Field(int number, byte[] value) => [.. Varint((ulong)(number << 3 | 2)), .. Varint((ulong)value.Length), .. value];
    private static byte[] Number(int number, ulong value) => [.. Varint((ulong)(number << 3)), .. Varint(value)];
    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do { var b = (byte)(value & 0x7F); value >>= 7; bytes.Add(value == 0 ? b : (byte)(b | 0x80)); } while (value != 0);
        return bytes.ToArray();
    }
    private static byte[] Site(int type, string value) => Field(2, [.. Number(1, (ulong)type), .. Field(2, Encoding.UTF8.GetBytes(value))]);
    private static byte[] GeositeFixture() =>
    [
        .. Field(1, [.. Field(1, Encoding.UTF8.GetBytes("CATEGORY-RU")), .. Site(2, "yandex.ru"), .. Site(3, "gosuslugi.ru")]),
        .. Field(1, [.. Field(1, Encoding.UTF8.GetBytes("google")), .. Site(2, "google.com")])
    ];
    private static byte[] GeoipFixture() =>
        Field(1, [.. Field(1, Encoding.UTF8.GetBytes("PRIVATE")), .. Field(2, [.. Field(1, [192, 168, 0, 0]), .. Number(2, 16)])]);
}
