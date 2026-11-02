namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientTrafficChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Idle traffic timer sleeps until local midnight instead of waking once per second", () => Sync(() =>
        {
            using var owner = new ClientRuntime(new ConnectionCore());
            Assert(owner.Traffic.NextDelay(new DateTime(2026, 11, 2, 12, 0, 0)) == TimeSpan.FromHours(12));
            Assert(owner.Traffic.NextDelay(new DateTime(2026, 11, 2, 23, 59, 59)) == TimeSpan.FromSeconds(1));
        }));
        await check("Application traffic accounting rolls over the local day without a window", () => Sync(() =>
        {
            using var owner = new ClientRuntime(new ConnectionCore());
            owner.Workspace.TrafficDay = "2000-01-01"; owner.Workspace.TrafficToday = 500; owner.Workspace.TrafficHours[0] = 500;
            owner.Traffic.Tick();
            Assert(owner.Workspace.TrafficDay == DateTime.Now.ToString("yyyy-MM-dd") && owner.Workspace.TrafficToday == 0 && owner.Workspace.TrafficHours.All(h => h == 0));
        }));
        await check("Application program accounting preserves deltas and journals independently of the view", () => Sync(() =>
        {
            using var owner = new ClientRuntime(new ConnectionCore());
            CoreConnection Row(long up, long down) => new(@"C:\Apps\Fixture.exe", false, "example.invalid", "192.0.2.1", 443, [], "one", up, down);
            owner.Traffic.RecordCoreConnections([Row(10, 100)]);
            owner.Traffic.RecordCoreConnections([Row(20, 200)]);
            var report = owner.Workspace.TrafficHistory.Report(DateTime.Now, 1);
            Assert(report.Programs.Single().Upload == 20 && report.Programs.Single().Download == 200);
            Assert(owner.Traffic.Journal.Recent(DateTime.UtcNow).Count > 0);
        }));
    }
}
