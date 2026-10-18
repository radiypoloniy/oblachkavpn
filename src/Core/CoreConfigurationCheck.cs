using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Oblachka;

public static class CoreConfigurationCheck
{
    public static async Task ValidateTunAsync(NetworkLeaseRequest request, CancellationToken token, bool ipv6 = true, System.Net.IPAddress? localDns = null, string? ruleSet = null, string? programSets = null, string? listsFolder = null)
    {
        var executable = TunCore.Program();
        await TunCore.VerifyAsync(executable, token);
        await ValidateAsync(new(CoreKind.SingBox, executable), SingBoxTunConfig.Create(request, ipv6, localDns, ruleSet, programSets, listsFolder), token);
    }

    internal static async Task ValidateAsync(CoreAdapter adapter, JsonObject configuration, CancellationToken token)
    {
        var start = new ProcessStartInfo(adapter.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("XRAY_", StringComparison.OrdinalIgnoreCase) ||
            k.StartsWith("SING_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        foreach (var argument in adapter.ValidationArguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var job = new ProcessJob();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            process.Start(); job.Attach(process);
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(configuration.ToJsonString().AsMemory(), timeout.Token); process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(output, error);
            if (process.ExitCode != 0) throw new UserError("Ядро отклонило конфигурацию. Проверьте совместимость параметров.");
        }
        finally { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } }
    }
}
