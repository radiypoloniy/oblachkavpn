namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientSelectionChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Headless auto selection respects pool exclusions, free servers and rule-set limits", () =>
        {
            using var owner = new ClientRuntime(new ConnectionCore()); var data = owner.Profiles;
            var a = Profiles.Parse(link); var b = Profiles.Parse(link.Replace("example.com", "second.invalid").Replace("#Test", "#Second"));
            var free = Profiles.Parse(link.Replace("example.com", "free.invalid")) with { Source = ProfileSource.Free, Name = "Free", Group = FreeServers.GroupId };
            data.Imported = [a, b, free]; data.Selected = a; data.AutoSelect = true;
            data.MarkMeasured(a, 1000); data.MarkMeasured(b, 1); data.MarkMeasured(free, 1);
            Assert(owner.Selection.BeforeConnect() == b);
            data.AutoPool.Set(b, false); Assert(owner.Selection.BeforeConnect() == a);
            data.AutoPool.Set(b, true); data.RuleSetServers = [ServerMachines.Hash(a)];
            Assert(owner.Selection.BeforeConnect() == a);
            data.AutoSelect = false; Assert(owner.Selection.BeforeConnect() == a);
            return Task.CompletedTask;
        });
        await check("Headless machine selection preserves the working protocol remembered for this network", () =>
        {
            using var owner = new ClientRuntime(new ConnectionCore()); var data = owner.Profiles;
            var a = Profiles.Parse(link); var b = Profiles.Parse(link.Replace("#Test", "#Sibling").Replace("type=xhttp", "type=ws"));
            Assert(ServerMachines.Key(a) == ServerMachines.Key(b) && Profiles.Fingerprint(a) != Profiles.Fingerprint(b));
            data.Imported = [a, b]; data.Selected = a; data.MachineMode = true;
            data.MarkMeasured(a, 1000); data.MarkMeasured(b, 1);
            owner.Selection.NetworkKey = "fixture-network";
            owner.Workspace.MachineMemory[owner.Selection.NetworkKey] = new() { [ServerMachines.Hash(a)] = Profiles.Fingerprint(a) };
            Assert(owner.Selection.BeforeConnect() == a);
            data.MarkMeasured(a, null); Assert(owner.Selection.BeforeConnect() == b);
            return Task.CompletedTask;
        });
        await check("Pause uses current auto-selection results when resuming rather than its old server", async () =>
        {
            var now = DateTimeOffset.UtcNow; var core = new ConnectionCore();
            using var owner = new ClientRuntime(core, new TunnelHostFixture(), pauseNow: () => now);
            var a = Profiles.Parse(link); var b = Profiles.Parse(link.Replace("example.com", "second.invalid"));
            owner.Profiles.Imported = [a, b]; owner.Profiles.AutoSelect = true;
            await owner.Connections.StartAsync(a); await owner.Pause.PauseAsync(5);
            now = now.AddMinutes(6); owner.Profiles.MarkMeasured(a, 1000); owner.Profiles.MarkMeasured(b, 1);
            await owner.Pause.PollAsync(); Assert(core.Current == b && owner.Profiles.Selected == b);
        });
    }
}
