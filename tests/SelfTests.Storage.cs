using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task StorageChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        var path = Path.Combine(root, "encrypted-state.bin");
        var store = new ClientStateStore(path);
        var profile = Profiles.Parse(link);
        var snapshot = new ClientSnapshot(1, new[] { new SavedProfile("🇪🇺Обход 2", profile.Outbound) }, Profiles.Fingerprint(profile),
            new[] { "https://fixture.invalid/sub/secret-fixture-token" }, 0, true, true);
        await check("DPAPI state roundtrip restores secrets without plaintext on disk", () => Sync(() =>
        {
            Assert(store.Load() is null);
            store.Save(snapshot);
            var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert(!raw.Contains("secret-fixture-token") && !raw.Contains("11111111-1111-4111") && !raw.Contains("example.com"));
            var loaded = store.Load()!;
            Assert(loaded.SubscriptionSources.SequenceEqual(snapshot.SubscriptionSources) && loaded.StayInTray && loaded.AutoRecover);
            var restored = ClientStateStore.RestoreProfiles(loaded).Single();
            Assert(restored.Name == "🇪🇺Обход 2" && Profiles.Fingerprint(restored) == Profiles.Fingerprint(profile));
        }));
        await check("Connect on launch defaults on, survives a roundtrip and old snapshots", () => Sync(() =>
        {
            Assert(snapshot.ConnectOnLaunch && !snapshot.WasConnected);
            var legacy = System.Text.Json.JsonSerializer.Deserialize<ClientSnapshot>("{\"Version\":1,\"Profiles\":[],\"SubscriptionSources\":[],\"PreferredSource\":0,\"StayInTray\":true,\"AutoRecover\":true}")!;
            Assert(legacy.ConnectOnLaunch && !legacy.WasConnected);
            store.Save(snapshot with { ConnectOnLaunch = false, WasConnected = true });
            var loaded = store.Load()!;
            Assert(!loaded.ConnectOnLaunch && loaded.WasConnected);
            store.Save(snapshot);
        }));
        await check("Invalid saved-state version is rejected before replacing valid encrypted data", () => Sync(() =>
        {
            var before = SHA256.HashData(File.ReadAllBytes(path));
            ExpectUserError(() => store.Save(snapshot with { Version = 999 }));
            Assert(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))));
        }));
        await check("Tampered encrypted state is rejected without deleting the source file", () => Sync(() =>
        {
            var brokenPath = Path.Combine(root, "tampered-state.bin");
            var bytes = File.ReadAllBytes(path); bytes[bytes.Length / 2] ^= 0x40;
            File.WriteAllBytes(brokenPath, bytes);
            ExpectUserError(() => new ClientStateStore(brokenPath).Load());
            Assert(File.ReadAllBytes(brokenPath).SequenceEqual(bytes));
        }));
        await check("Encrypted state preserves mixed-core profiles and refuses invalid saved outbound", () => Sync(() =>
        {
            var native = Profiles.Parse("{\"type\":\"vless\",\"server\":\"fixture.invalid\",\"server_port\":443,\"uuid\":\"11111111-1111-4111-8111-111111111111\",\"tls\":{\"enabled\":true}}");
            store.Save(snapshot with { Profiles = new[] { snapshot.Profiles[0], new SavedProfile("Рабочий сервер", native.Outbound) } });
            var restored = ClientStateStore.RestoreProfiles(store.Load()!);
            Assert(restored.Count == 2 && restored[0].Core == CoreKind.Xray && restored[1].Core == CoreKind.SingBox);
            ExpectUserError(() => store.Save(snapshot with { Profiles = new[] { new SavedProfile("Invalid", new JsonObject { ["protocol"] = "freedom" }) } }));
        }));
        await check("Saved state migrates between install folders without deleting legacy files", () => Sync(() =>
        {
            var oldBytes = File.ReadAllBytes(path);
            var stablePath = Path.Combine(root, "stable", "client-state.bin");
            var stable = new ClientStateStore(stablePath);
            var migrated = stable.LoadWithMigration(new[] { path });
            Assert(migrated is not null && migrated.Profiles.Length == 2);
            Assert(File.ReadAllBytes(path).SequenceEqual(oldBytes));
            Assert(stable.LoadWithMigration(new[] { "nonexistent-legacy-path" })!.Profiles.Length == 2);
        }));
        await check("Traffic history survives encrypted state and invalid counters are rejected", () => Sync(() =>
        {
            var day = new TrafficDayStats("2026-10-12", 100, 900, new long[24], new long[24],
                [new TrafficProgramStats(@"C:\Apps\Browser.exe", 100, 400)]);
            store.Save(snapshot with { TrafficHistory = [day] });
            var loaded = store.Load()!.TrafficHistory!.Single();
            Assert(loaded.Day == day.Day && loaded.Upload == 100 && loaded.Download == 900 && loaded.Programs.Single().Path == day.Programs[0].Path);
            var brokenHours = new long[24]; brokenHours[3] = -1;
            ExpectUserError(() => store.Save(snapshot with { TrafficHistory = [day with { DownloadHours = brokenHours }] }));
        }));
    }
}
