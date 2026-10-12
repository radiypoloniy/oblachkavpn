namespace RoutePilot;

public static partial class SelfTests
{
    // Узлы сервиса и сторонние различаются по источнику. От источника зависит, что
    // клиенту позволено делать автоматически: переключать узел и измерять можно только
    // своё, за чужие конфигурации сервис не отвечает.
    private static async Task SourceChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        var host = ServiceBrand.SubscriptionHosts[0];
        await check("Source is taken from the addresses the list was loaded from", () => Sync(() =>
        {
            Assert(ServiceKey.SourceOf(new[] { "https://" + host + "/sub/abcdefghij" }) == ProfileSource.Service);
            Assert(ServiceKey.SourceOf(new[] { "https://sub.other.invalid/sub/abcdefghij" }) == ProfileSource.External);
            // Смешанный набор адресов считается сторонним целиком.
            Assert(ServiceKey.SourceOf(new[] { "https://" + host + "/sub/abcdefghij", "https://sub.other.invalid/sub/x" }) == ProfileSource.External);
            Assert(ServiceKey.SourceOf(Array.Empty<string>()) == ProfileSource.External);
            // Разбор подписки источника не знает: его проставляет вызывающий.
            Assert(Subscriptions.Parse(link).Profiles[0].Source == ProfileSource.External);
            Assert(Subscriptions.Parse(link).WithSource(ProfileSource.Service).Profiles[0].IsService);
        }));
        await check("Route search and recovery never move the user off their own service", () => Sync(() =>
        {
            var mine = Profiles.Parse(link) with { Name = "Финляндия", Source = ProfileSource.Service };
            var alsoMine = Profiles.Parse(link.Replace("example.com", "node.example")) with { Name = "Сербия", Source = ProfileSource.Service };
            var foreign = Profiles.Parse(link.Replace("example.com", "foreign.example")) with { Name = "Чужой", Source = ProfileSource.External };
            var all = new[] { mine, alsoMine, foreign };

            // Выбран свой узел: перебираются оба своих, чужой не рассматривается.
            var fromService = RouteCandidates.For(all, mine);
            Assert(fromService.Length == 2 && fromService[0] == 0 && fromService[1] == 1);
            Assert(RouteCandidates.StartAt(fromService, all, alsoMine) == 1);

            // Выбран чужой узел: перебор ограничен им самим, повторы есть, смены узла нет.
            var fromForeign = RouteCandidates.For(all, foreign);
            Assert(fromForeign.Length == 1 && fromForeign[0] == 2);
            Assert(RouteCandidates.StartAt(fromForeign, all, foreign) == 0);

            // Своих узлов нет вовсе: подбор не остаётся без кандидатов.
            var onlyForeign = new[] { foreign };
            Assert(RouteCandidates.For(onlyForeign, foreign).Length == 1);
        }));
        await check("Saved state keeps both sets apart and restores the source of each node", async () =>
        {
            var store = new ClientStateStore(Path.Combine(root, "sources-state.bin"));
            var mine = Profiles.Parse(link) with { Name = "Финляндия", Source = ProfileSource.Service };
            var foreign = Profiles.Parse(link.Replace("example.com", "foreign.example")) with { Name = "Чужой", Source = ProfileSource.External };
            var saved = new[] { mine, foreign }
                .Select(p => new SavedProfile(p.Name, (System.Text.Json.Nodes.JsonObject)p.Outbound.DeepClone()) { Source = p.Source }).ToArray();
            store.Save(new ClientSnapshot(1, saved, Profiles.Fingerprint(mine),
                new[] { "https://" + host + "/sub/abcdefghij" }, 0, false, false)
            { SelectedName = "Финляндия", ExternalSources = new[] { "https://sub.other.invalid/sub/abcdefghij" } });
            var restored = ClientStateStore.RestoreProfiles(store.Load()!);
            Assert(restored.Count == 2 && restored[0].IsService && !restored[1].IsService);
            Assert(store.Load()!.ExternalSources.Length == 1);
            await Task.CompletedTask;
        });
        await check("Snapshot without sources infers them from the saved subscription addresses", async () =>
        {
            var store = new ClientStateStore(Path.Combine(root, "legacy-sources-state.bin"));
            var outbound = (System.Text.Json.Nodes.JsonObject)Profiles.Parse(link).Outbound.DeepClone();
            // Снимок 0.8: поля Source у профиля нет.
            store.Save(new ClientSnapshot(1, new[] { new SavedProfile("Финляндия", outbound) }, null,
                new[] { "https://" + host + "/sub/abcdefghij" }, 0, false, false));
            Assert(ClientStateStore.RestoreProfiles(store.Load()!)[0].IsService);

            var foreignStore = new ClientStateStore(Path.Combine(root, "legacy-foreign-state.bin"));
            foreignStore.Save(new ClientSnapshot(1, new[] { new SavedProfile("Чужой", (System.Text.Json.Nodes.JsonObject)outbound.DeepClone()) }, null,
                new[] { "https://sub.other.invalid/sub/abcdefghij" }, 0, false, false));
            Assert(!ClientStateStore.RestoreProfiles(foreignStore.Load()!)[0].IsService);
            await Task.CompletedTask;
        });
    }
}
