namespace RoutePilot;

public static partial class SelfTests
{
    // Сервис отдаёт один вход под двумя именами: «Авто — оптимальное подключение» —
    // балансировщик, за которым стоит та же точка входа, что и у строки со страной.
    // Для пользователя это две строки списка, для конфигурации — одна и та же.
    private static async Task AliasChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Identical configurations under different names stay separate list rows", () => Sync(() =>
        {
            var balancer = Profiles.Parse(link) with { Name = "Авто" };
            var country = Profiles.Parse(link) with { Name = "Германия" };
            var profiles = new[] { balancer, country };
            Assert(Profiles.Fingerprint(balancer) == Profiles.Fingerprint(country));
            Assert(ProfileSelection.Identity(balancer) != ProfileSelection.Identity(country));
            Assert(ProfileSelection.IndexOf(profiles, country) == 1);
            Assert(ProfileSelection.IndexOf(profiles, balancer) == 0);
            Assert(ProfileSelection.Find(profiles, country) == 1);
        }));
        await check("Renamed node keeps the selection through the fingerprint fallback", () => Sync(() =>
        {
            var before = Profiles.Parse(link) with { Name = "Германия" };
            var renamed = new[] { Profiles.Parse(link) with { Name = "Германия (новая)" } };
            Assert(ProfileSelection.IndexOf(renamed, before) == 0);
        }));
        await check("Saved selection restores the exact row when two rows share a configuration", async () =>
        {
            var store = new ClientStateStore(Path.Combine(root, "alias-state.bin"));
            var outbound = Profiles.Parse(link).Outbound;
            var saved = new[]
            {
                new SavedProfile("Авто", (System.Text.Json.Nodes.JsonObject)outbound.DeepClone()),
                new SavedProfile("Германия", (System.Text.Json.Nodes.JsonObject)outbound.DeepClone())
            };
            var fingerprint = Profiles.Fingerprint(Profiles.Parse(link));
            store.Save(new ClientSnapshot(1, saved, fingerprint, Array.Empty<string>(), 0, false, false) { SelectedName = "Германия" });
            var snapshot = store.Load()!;
            var restored = ClientStateStore.RestoreProfiles(snapshot);
            var selected = restored.FirstOrDefault(p => p.Name == snapshot.SelectedName && Profiles.Fingerprint(p) == snapshot.SelectedFingerprint)
                ?? restored.FirstOrDefault(p => Profiles.Fingerprint(p) == snapshot.SelectedFingerprint);
            Assert(selected is not null && selected.Name == "Германия");
            // Снимок без имени (сохранён прежней версией) по-прежнему читается.
            store.Save(new ClientSnapshot(1, saved, fingerprint, Array.Empty<string>(), 0, false, false));
            var legacy = store.Load()!;
            Assert(legacy.SelectedName is null && ClientStateStore.RestoreProfiles(legacy).Count == 2);
            await Task.CompletedTask;
        });
    }
}
