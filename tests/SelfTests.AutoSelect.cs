namespace Oblachka;

public static partial class SelfTests
{
    // Автовыбор: оценка — задержка плюс скорость, текущий сервер держится; видео YouTube
    // проверяется по скорости, а не по первому ответу.
    private static async Task AutoSelectScoreChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Auto-select weighs speed as well as delay and keeps the current server unless another is clearly better", () => Sync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var nodes = new[] { Node("203.0.113.71", name: "Hysteria"), Node("203.0.113.72", name: "Germany"), Node("203.0.113.73", name: "Finland") };
            ServerMeasurement M(Profile p, long ms, long bps) => new(Profiles.Fingerprint(p), now, true, ms) { Throughput = bps };
            // Hysteria: 60 мс, но 300 КБ/с; Германия: 140 мс и 6 МБ/с — она и лучше для обычной работы.
            var measured = new Dictionary<string, ServerMeasurement>
            {
                [Profiles.Fingerprint(nodes[0])] = M(nodes[0], 60, 300_000),
                [Profiles.Fingerprint(nodes[1])] = M(nodes[1], 140, 6_000_000),
                [Profiles.Fingerprint(nodes[2])] = M(nodes[2], 150, 5_500_000)
            };
            Assert(ServerMeasurements.Score(measured[Profiles.Fingerprint(nodes[1])]) < ServerMeasurements.Score(measured[Profiles.Fingerprint(nodes[0])]));
            Assert(ServerMeasurements.Best(nodes, measured, now) == 1);
            // Финляндия почти так же хороша — уже работаем на ней, переключаться незачем.
            Assert(ServerMeasurements.Best(nodes, measured, now, nodes[2]) == 2);
            // А с Hysteria уходим: она хуже заметно.
            Assert(ServerMeasurements.Best(nodes, measured, now, nodes[0]) == 1);
            // Без замера скорости оценка остаётся по задержке.
            Assert(ServerMeasurements.Score(new("x", now, true, 100)) is > 100 and < 600);
            Assert(ServerMeasurements.Score(new("x", now, false, 0)) == double.MaxValue && ServerMeasurements.Score(new("x", now, true, 3) { Handshake = true }) == double.MaxValue);
        }));
        await check("Auto-select measures speed only for the fastest by delay and does not credit unmeasured servers with a default speed", () => Sync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var nodes = Enumerable.Range(0, 6).Select(i => Node("203.0.113." + (80 + i), name: "N" + i)).ToArray();
            ServerMeasurement M(Profile p, long ms, long bps = 0, bool ok = true) => new(Profiles.Fingerprint(p), now, ok, ms) { Throughput = bps };
            var measured = new Dictionary<string, ServerMeasurement>
            {
                [Profiles.Fingerprint(nodes[0])] = M(nodes[0], 200), [Profiles.Fingerprint(nodes[1])] = M(nodes[1], 40),
                [Profiles.Fingerprint(nodes[2])] = M(nodes[2], 90), [Profiles.Fingerprint(nodes[3])] = M(nodes[3], 0, ok: false),
                [Profiles.Fingerprint(nodes[4])] = M(nodes[4], 60), [Profiles.Fingerprint(nodes[5])] = M(nodes[5], 300)
            };
            // Три лучших по задержке среди ответивших, плюс текущий, если он не среди них.
            Assert(ServerMeasurements.ThroughputTargets(nodes, measured, now).SequenceEqual(new[] { nodes[1], nodes[4], nodes[2] }));
            Assert(ServerMeasurements.ThroughputTargets(nodes, measured, now, nodes[5]).SequenceEqual(new[] { nodes[1], nodes[4], nodes[2], nodes[5] }));
            Assert(ServerMeasurements.ThroughputTargets(nodes, measured, now, nodes[3]).Count == 3);
            // Медленная сеть: измеренные 800 КБ/с. Сервер без замера не получает условные 2,5 МБ/с и не
            // обгоняет их только за счёт этого — ему приписывается худшая измеренная скорость.
            measured[Profiles.Fingerprint(nodes[1])] = M(nodes[1], 40, 800_000);
            measured[Profiles.Fingerprint(nodes[4])] = M(nodes[4], 60, 900_000);
            measured[Profiles.Fingerprint(nodes[2])] = M(nodes[2], 90, 850_000);
            Assert(ServerMeasurements.Best(nodes, measured, now) == 4 && ServerMeasurements.Score(measured[Profiles.Fingerprint(nodes[0])]) < ServerMeasurements.Score(measured[Profiles.Fingerprint(nodes[4])]));
            Assert(ServerMeasurements.FallbackThroughput(measured.Values) == 800_000 && ServerMeasurements.FallbackThroughput([]) == ServerMeasurements.DefaultThroughput);
            // Прежний короткий замер дал бы TCP-серверу 1 МБ/с в медленном старте, а честный — 12 МБ/с:
            // Hysteria с меньшей задержкой и 5 МБ/с больше не выигрывает.
            var hy = Node("203.0.113.90", name: "Hysteria"); var tcp = Node("203.0.113.91", name: "REALITY");
            var fair = new Dictionary<string, ServerMeasurement> { [Profiles.Fingerprint(hy)] = M(hy, 45, 5_000_000), [Profiles.Fingerprint(tcp)] = M(tcp, 70, 12_000_000) };
            Assert(ServerMeasurements.Best([hy, tcp], fair, now) == 1);
            Assert(ServerMeasurements.ThroughputBytes >= 4 * 1024 * 1024 && ServerMeasurements.ThroughputWindow >= TimeSpan.FromSeconds(3) && ServerMeasurements.ThroughputTarget.Query.Contains("8388608"));
        }));
        await check("Metered networks are recognised by Windows cost flags and the lookup never throws", () => Sync(() =>
        {
            // 0x1 — безлимит; 0x2 фиксированный объём, 0x4 оплата за трафик, 0x40000 роуминг.
            Assert(!NetworkCost.IsMetered(0x1) && NetworkCost.IsMetered(0x2) && NetworkCost.IsMetered(0x4) && NetworkCost.IsMetered(0x1 | 0x40000));
            _ = NetworkCost.Metered();
        }));
        await check("YouTube video check needs half a megabyte at 720p speed, not just the first bytes", () => Sync(() =>
        {
            Assert(ZapretProbe.FastEnough(1_000_000, TimeSpan.FromSeconds(0.5)));
            Assert(!ZapretProbe.FastEnough(64 * 1024, TimeSpan.FromSeconds(0.1)));
            Assert(!ZapretProbe.FastEnough(600 * 1024, TimeSpan.FromSeconds(6)));
            Assert(ZapretProbe.Heavy.SequenceEqual(new[] { 2 }) && ZapretProbe.Targets[2].Host == "redirector.googlevideo.com");
        }));
    }
}
