using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // «Подобрать лучшее»: поправки трогают только клиентские поля и не меняют сохранённый профиль;
    // подбор берёт поправку, только если она стабильно и заметно лучше сервера как в подписке.
    private static async Task TuneChecksAsync(Func<string, Func<Task>, Task> check)
    {
        const string reality = "vless://6f1c2e0a-5b7d-4c3e-9a1f-0b2c3d4e5f60@203.0.113.7:443?security=reality&sni=www.example.com&pbk=SbVKOEMjK0sIlbwg4akyBg5mL5KZwwB-ed4eEE7YnRc&fp=chrome&type=tcp#R";
        var hy2 = SingBoxProfiles.Parse(new JsonObject
        {
            ["type"] = "hysteria2", ["server"] = "203.0.113.8", ["server_port"] = 443, ["password"] = "p", ["up_mbps"] = 50, ["down_mbps"] = 200,
            ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "example.com" }
        }, "H");

        await check("Server tweaks change only client-side fields and leave the saved profile as it was", () => Sync(() =>
        {
            var server = Profiles.Parse(reality);
            var before = server.Outbound.ToJsonString();
            var tuned = new ServerTweak(Fingerprint: "firefox").Apply(server);
            Assert((string?)tuned.Outbound["streamSettings"]!["realitySettings"]!["fingerprint"] == "firefox" && server.Outbound.ToJsonString() == before);
            Assert(tuned.Outbound["streamSettings"]!["realitySettings"]!["publicKey"]!.ToJsonString() == server.Outbound["streamSettings"]!["realitySettings"]!["publicKey"]!.ToJsonString());
            // Варианты: как есть, другие отпечатки (без нынешнего chrome), нарезка рукопожатия.
            var candidates = ServerTweak.Candidates(server);
            Assert(candidates[0].IsEmpty && candidates.All(c => c.Fingerprint != "chrome") && candidates.Count(c => c.Fingerprint is not null) == 3 && candidates.Any(c => c.Fragment));
            // Hysteria2: отпечатка и нарезки нет, есть скорость по каналу вместо заданной.
            var quic = ServerTweak.Candidates(hy2);
            Assert(quic.Count == 2 && quic[1].Bbr && new ServerTweak(Bbr: true).Apply(hy2).Outbound["up_mbps"] is null && hy2.Outbound["up_mbps"] is not null);
            Assert(new ServerTweak(Fingerprint: "safari", Fragment: true).Describe() == "отпечаток Safari, нарезка рукопожатия" && new ServerTweak().Describe() == "как в подписке");
            Assert(!new ServerTweak(Fingerprint: "netscape").Valid);
        }));

        await check("Saved tweaks reach the tunnel core; a tweak with fragmentation stays on the window core", () => Sync(() =>
        {
            var server = Profiles.Parse(reality);
            try
            {
                ServerTweak.Lookup = p => ServerTweak.Key(p) == ServerTweak.Key(server) ? new ServerTweak(Fingerprint: "firefox") : null;
                Assert(DirectOutbound.From(server)?.ToJsonString().Contains("firefox") == true);
                ServerTweak.Lookup = _ => new ServerTweak(Fragment: true);
                Assert(DirectOutbound.From(server) is null);
            }
            finally { ServerTweak.Lookup = _ => null; }
        }));

        await check("Tuning keeps a tweak only when it is steadily and clearly better", async () =>
        {
            var a = Profiles.Parse(reality.Replace("#R", "#A"));
            var b = Profiles.Parse(reality.Replace("#R", "#B").Replace("203.0.113.7", "203.0.113.9"));
            ServerMeasurement M(long ms, long mbps, bool ok = true) => new("x", DateTimeOffset.UtcNow, ok, ms) { Throughput = mbps * 1_000_000 };
            async Task<TuneResult?> Run(Func<Profile, ServerTweak, int, ServerMeasurement> table)
            {
                var calls = new Dictionary<(string, ServerTweak), int>();
                return await TuneSearch.RunAsync([a, b], (server, tweak, _) =>
                {
                    var n = calls[(server.Name, tweak)] = calls.GetValueOrDefault((server.Name, tweak)) + 1;
                    return Task.FromResult(table(server, tweak, n));
                }, null, CancellationToken.None);
            }
            // Firefox на A быстрее вчетверо — берётся; «было» — A как в подписке.
            var won = await Run((s, t, _) => s == b ? M(300, 2) : t.Fingerprint == "firefox" ? M(100, 8) : M(100, 2));
            Assert(won is { Tweak.Fingerprint: "firefox" } && won.Server == a && won.Before.Throughput == 2_000_000 && won.After.Throughput == 8_000_000);
            // Чуть лучше — в пределах шума: сервер остаётся как в подписке.
            var noise = await Run((s, t, _) => s == b ? M(300, 2) : t.Fingerprint == "safari" ? M(95, 2) : M(100, 2));
            Assert(noise is { Tweak.IsEmpty: true } && noise.Server == a);
            // Быстрый, но раз не ответил на подтверждении — выбывает.
            var flaky = await Run((s, t, n) => s == b ? M(300, 2) : t.Fragment ? M(80, 10, ok: n != 2) : M(100, 2));
            Assert(flaky is { Tweak.Fragment: false });
            Assert(await Run((_, _, _) => M(0, 0, ok: false)) is null);
        });
    }
}
