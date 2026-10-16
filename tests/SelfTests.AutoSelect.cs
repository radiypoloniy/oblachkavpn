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
