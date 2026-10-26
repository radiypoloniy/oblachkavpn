namespace Oblachka;

public static partial class SelfTests
{
    // Поведение по сетям (NetworkPolicy) и чтение сети у Windows (NetworkCost.Network).
    private static async Task NetworkChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Networks: VPN turns on by itself only in «always VPN» or unknown public networks, never over a person's choice", () => Sync(() =>
        {
            bool Decide(NetworkMode mode, bool known = true, bool isPublic = false, bool secure = false, bool on = false, bool paused = false, bool stopped = false) =>
                NetworkPolicy.ShouldConnect(mode, known, isPublic, secure, on, paused, stopped);
            Assert(Decide(NetworkMode.Vpn) && !Decide(NetworkMode.Trusted, isPublic: true, secure: true) && !Decide(NetworkMode.Usual));
            // Незнакомая общественная — только с включённой настройкой; знакомая «обычная» — нет.
            Assert(Decide(NetworkMode.Usual, known: false, isPublic: true, secure: true) && !Decide(NetworkMode.Usual, known: false, isPublic: true));
            Assert(!Decide(NetworkMode.Usual, known: false, isPublic: false, secure: true));
            // Уже включён, пауза, выключил сам в этой сети — не трогаем.
            Assert(!Decide(NetworkMode.Vpn, on: true) && !Decide(NetworkMode.Vpn, paused: true) && !Decide(NetworkMode.Vpn, stopped: true));
        }));
        await check("Networks: saved list keeps only valid keys and decisions, names are cleaned and cut", () => Sync(() =>
        {
            var key = NetworkPolicy.Key(Guid.NewGuid());
            Assert(NetworkPolicy.IsKey(key) && key != NetworkPolicy.Key(Guid.NewGuid()));
            var restored = NetworkPolicy.Restore(new()
            {
                [key] = new("Дом\u0007\n", NetworkMode.Vpn), ["не ключ"] = new("x", NetworkMode.Vpn),
                [NetworkPolicy.Key(Guid.NewGuid())] = new("y", NetworkMode.Usual), [NetworkPolicy.Key(Guid.NewGuid())] = new(new string('я', 80), NetworkMode.Trusted)
            });
            Assert(restored.Count == 2 && restored[key].Name == "Дом" && restored.Values.All(n => n.Name.Length <= NetworkPolicy.MaxName + 1));
            Assert(NetworkPolicy.CleanName("  ") == "Без имени" && NetworkPolicy.Restore(null).Count == 0);
            // «Обычная» с набором правил хранится; «обычная» без набора и негодное имя набора — нет.
            var home = NetworkPolicy.Key(Guid.NewGuid());
            var kept = NetworkPolicy.Restore(new()
            {
                [home] = new("Дом", NetworkMode.Usual, "Дом"), [NetworkPolicy.Key(Guid.NewGuid())] = new("x", NetworkMode.Vpn, " пробел"),
                [NetworkPolicy.Key(Guid.NewGuid())] = new("y", (NetworkMode)7)
            });
            Assert(kept.Count == 1 && kept[home].RuleSet == "Дом" && !new KnownNetwork("z", NetworkMode.Usual).Worth);
            Assert(!System.Text.Json.JsonSerializer.Serialize(kept[home]).Contains("Worth"));
        }));
        await check("Networks: Windows reports the network of the physical adapter with a name", () => Sync(() =>
        {
            // Без сети (стенд без шлюза) проверять нечего; с сетью — идентификатор и имя должны прийти.
            if (NetworkIdentity.Physical() is null) return;
            var network = NetworkCost.Network();
            Assert(network is { } n && n.Id != Guid.Empty && n.Name.Length > 0);
        }));
    }
}
