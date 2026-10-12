using System.Text.Json;

namespace RoutePilot;

public static class LabCommand
{
    public static Task<int> RunAsync(string manifestPath, string reportPath) => RunWithAsync(manifestPath, reportPath, async plan =>
    {
        using var engine = new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), "");
        return await new LabRunner(engine).RunAsync(plan, CancellationToken.None);
    });

    internal static async Task<int> RunWithAsync(string manifestPath, string reportPath, Func<LabPlan, Task<LabResult>> run)
    {
        try
        {
            if (new FileInfo(manifestPath).Length > Profiles.MaxBytes) throw new UserError("План стенда больше 1 МБ.");
            var plan = LabPlan.Parse(await File.ReadAllTextAsync(manifestPath));
            var csvPath = Path.ChangeExtension(reportPath, ".csv");
            if (!Path.GetExtension(reportPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
                throw new UserError("Укажи отчёт с расширением .json.");
            // Reserve both new outputs before any network traffic. Existing files are untouched.
            using var json = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write);
            using var csv = new FileStream(csvPath, FileMode.CreateNew, FileAccess.Write);
            var result = await run(plan);
            await JsonSerializer.SerializeAsync(json, new
            {
                App = ServiceBrand.Label, GeneratedAt = DateTimeOffset.UtcNow,
                Measurement = "HTTP response headers through local SOCKS; not ping or bandwidth",
                SystemNetworkModified = false, Result = result
            }, new JsonSerializerOptions { WriteIndented = true });
            using var writer = new StreamWriter(csv, leaveOpen: true);
            await writer.WriteLineAsync("profile_index,core,repetition,outcome,startup_ms,request_ms");
            foreach (var attempt in result.Attempts)
                await writer.WriteLineAsync($"{attempt.ProfileIndex},{attempt.Core},{attempt.Repetition},{attempt.Outcome},{attempt.StartupMilliseconds},{attempt.RequestMilliseconds}");
            return result.Canceled ? 3 : result.Attempts.All(a => a.Outcome == "Success") ? 0 : 2;
        }
        catch (Exception ex) when (ex is UserError or IOException or UnauthorizedAccessException)
        { return 4; } // No raw exception output: manifests may contain credentials.
    }
}
