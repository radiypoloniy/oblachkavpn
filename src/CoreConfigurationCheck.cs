using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace RoutePilot;

public static class CoreConfigurationCheck
{
    public static async Task ValidateTunAsync(NetworkLeaseRequest request, CancellationToken token)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "tools", "sing-box.exe");
        await using (var input = File.OpenRead(executable))
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
            if (hash != "5C19BB3F8C12D4E85B3D47FE63123A7624B314B58DD7A8E32F51004F1C4D2FAF")
                throw new UserError("Ядро sing-box не соответствует закреплённой версии.");
        }
        await ValidateAsync(new(CoreKind.SingBox, executable), SingBoxTunConfig.Create(request), token);
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
