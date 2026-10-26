namespace Oblachka;

public static partial class SelfTests
{
    // Машины (ServerMachines): записи одной подписки с одним адресом — один сервер с разными протоколами.
    private static async Task MachineChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Machines: protocols of one address in one subscription are one server with a common name", () => Sync(() =>
        {
            Profile In(Profile p, string group = "g1", ProfileSource source = ProfileSource.Service) => p with { Group = group, Source = source };
            var reality = In(Node("203.0.113.50", name: "Германия VLESS"));
            var xhttp = In(Node("203.0.113.50", "xhttp", name: "Германия · XHTTP"));
            var hy2 = In(Profiles.Parse(hy2Link.Replace("example.com:443", "203.0.113.50:443").Replace("#HY2", "#" + Uri.EscapeDataString("Германия (Hysteria2)"))));
            var other = In(Node("203.0.113.51", name: "Финляндия"));
            var elsewhere = In(Node("203.0.113.50", name: "Германия VLESS"), group: "g2");
            var machines = ServerMachines.Group([reality, other, xhttp, hy2, elsewhere]);
            Assert(machines.Count == 3 && machines[0].SequenceEqual([reality, xhttp, hy2]) && machines[1].Single() == other && machines[2].Single() == elsewhere);
            Assert(ServerMachines.Title(machines[0]) == "Германия" && ServerMachines.Title([other]) == "Финляндия");
            Assert(ServerMachines.Title([In(Node("203.0.113.60", name: "Нидерланды 1")), In(Node("203.0.113.60", name: "Нидерланды 2"))]) == "Нидерланды");
            Assert(ServerMachines.Protocols(machines[0], hy2).StartsWith("Hysteria2 · ", StringComparison.Ordinal));
            // Бесплатные чужие узлы не объединяются, даже с одним адресом.
            var free1 = In(Node("203.0.113.70", name: "F1"), source: ProfileSource.Free);
            var free2 = In(Node("203.0.113.70", "xhttp", name: "F2"), source: ProfileSource.Free);
            Assert(ServerMachines.Group([free1, free2]).Count == 2 && ServerMachines.Hash(reality) == ServerMachines.Hash(xhttp) && ServerMachines.Hash(reality) != ServerMachines.Hash(elsewhere));
        }));
        await check("Machines: the protocol that worked in this network goes first unless it just failed; otherwise the best measurement", () => Sync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var a = Node("203.0.113.52", name: "A"); var b = Node("203.0.113.52", "xhttp", name: "B"); var c = Node("203.0.113.52", "grpc", name: "C");
            var members = new[] { a, b, c };
            var results = new Dictionary<Profile, ServerMeasurement?>();
            ServerMeasurement? Latest(Profile p) => results.GetValueOrDefault(p);
            ServerMeasurement Ok(Profile p, long ms) => new(Profiles.Fingerprint(p), now, true, ms) { Throughput = 5_000_000 };
            // Без замеров — первая в подписке; с замерами — лучшая.
            Assert(ServerMachines.Best(members, Latest, null, now) == a);
            results[a] = Ok(a, 300); results[c] = Ok(c, 60);
            Assert(ServerMachines.Best(members, Latest, null, now) == c);
            // Помнится B — она первая, даже без замера; её свежий провал — помнить незачем.
            Assert(ServerMachines.Best(members, Latest, Profiles.Fingerprint(b), now) == b);
            results[b] = new(Profiles.Fingerprint(b), now, false, 0);
            Assert(ServerMachines.Best(members, Latest, Profiles.Fingerprint(b), now) == c);
            // Старый провал не в счёт: в этой сети она работала.
            results[b] = new(Profiles.Fingerprint(b), now.AddHours(-2), false, 0);
            Assert(ServerMachines.Best(members, Latest, Profiles.Fingerprint(b), now) == b);
            // Все не ответили — первая; запись, которой уже нет в подписке, не мешает.
            foreach (var p in members) results[p] = new(Profiles.Fingerprint(p), now, false, 0);
            Assert(ServerMachines.Best(members, Latest, "00", now) == a);
        }));
        await check("Recovery: a stall (sending, nothing back for 5 s) triggers one early check; idle and background traffic do not", () => Sync(() =>
        {
            var stall = new TrafficStall();
            // Простой и фон — не обрыв.
            for (var i = 0; i < 20; i++) Assert(!stall.Feed(0, 0) && !stall.Feed(100, 0));
            // Отдача без приёма: на пятой секунде — сигнал, дальше молчим до конца эпизода.
            var fired = Enumerable.Range(0, 12).Select(_ => stall.Feed(4000, 0)).ToArray();
            Assert(fired.Count(f => f) == 1 && fired[TrafficStall.Seconds - 1]);
            // Ответ пришёл — эпизод кончился; новый обрыв снова даёт сигнал.
            Assert(!stall.Feed(4000, 900));
            Assert(Enumerable.Range(0, TrafficStall.Seconds).Select(_ => stall.Feed(4000, 0)).ToArray().Last());
        }));
    }
}
