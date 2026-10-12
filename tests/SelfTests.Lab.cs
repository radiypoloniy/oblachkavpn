using System.Text.Json;

namespace Oblachka;

public static partial class SelfTests
{
    private static async Task LabChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Lab plans reject excessive workloads, non-HTTPS targets and unknown options", () => Sync(() =>
        {
            string Plan(string target, int repeats) => JsonSerializer.Serialize(new { target, profiles = new[] { link }, repetitions = repeats });
            Assert(LabPlan.Parse(Plan("https://fixture.invalid/", 2)).Repetitions == 2);
            ExpectUserError(() => LabPlan.Parse(Plan("http://fixture.invalid/", 2)));
            ExpectUserError(() => LabPlan.Parse(Plan("https://fixture.invalid/", 11)));
            ExpectUserError(() => LabPlan.Parse("{\"target\":\"https://fixture.invalid/\",\"profiles\":[],\"unknown\":true}"));
        }));
        await check("Lab interleaves routes, classifies failures, stops each sample and exports no credentials", async () =>
        {
            var active = false; var starts = new List<string>(); var probes = 0;
            var profiles = new[] { Profiles.Parse(link), Profiles.Parse(link) with { Name = "Second" } };
            var runner = new LabRunner((p, _) => { Assert(!active); active = true; starts.Add(p.Name); return Task.CompletedTask; },
                (_, _) => Task.FromResult(++probes == 2 ? new ProbeResult(false, 17, "synthetic", ProbeFailure.TargetResponse) : new ProbeResult(true, 12, "synthetic")),
                () => active = false);
            var result = await runner.RunAsync(new(new Uri("https://fixture.invalid/"), profiles, 2, 0), CancellationToken.None);
            Assert(!active && starts.SequenceEqual(new[] { "Test", "Second", "Test", "Second" }));
            Assert(result.Attempts.Count == 4 && result.Summary[1].SuccessRate == 0.5 && result.Summary[0].MedianRequestMilliseconds == 12);
            var report = JsonSerializer.Serialize(result);
            Assert(!report.Contains("example.com") && !report.Contains("11111111") && !report.Contains("Second"));
        });
        await check("Lab cancellation preserves partial results and terminates the current sample", async () =>
        {
            using var cancel = new CancellationTokenSource(); var active = false;
            var runner = new LabRunner((_, ct) => { active = true; cancel.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
                (_, _) => throw new InvalidOperationException(), () => active = false);
            var result = await runner.RunAsync(new(new Uri("https://fixture.invalid/"), new[] { Profiles.Parse(link) }, 3, 0), cancel.Token);
            Assert(!active && result.Canceled && result.Attempts.Count == 1 && result.Attempts[0].Outcome == "Canceled");
        });
        await check("Lab command writes sanitized JSON/CSV and refuses to overwrite a report before running", async () =>
        {
            var planPath = Path.Combine(root, "synthetic-lab-plan.json");
            var reportPath = Path.Combine(root, "synthetic-lab-report.json");
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(new { target = "https://fixture.invalid/", profiles = new[] { link } }));
            var calls = 0;
            Task<LabResult> Run(LabPlan _) { calls++; return Task.FromResult(new LabResult(false,
                new[] { new LabAttempt(0, "Xray", 1, "Success", 15, 12) }, new[] { new LabSummary(0, 1, 1, 1, 12) })); }
            Assert(await LabCommand.RunWithAsync(planPath, reportPath, Run) == 0 && calls == 1);
            var before = await File.ReadAllTextAsync(reportPath);
            Assert(!before.Contains("example.com") && !before.Contains("11111111") && !before.Contains("fixture.invalid"));
            var csv = await File.ReadAllLinesAsync(Path.ChangeExtension(reportPath, ".csv"));
            Assert(csv.Length == 2 && csv[1] == "0,Xray,1,Success,15,12");
            Assert(await LabCommand.RunWithAsync(planPath, reportPath, Run) == 4 && calls == 1);
            Assert(await File.ReadAllTextAsync(reportPath) == before);
        });
    }
}
