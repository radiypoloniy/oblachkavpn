using System.Diagnostics;

namespace RoutePilot;

public static partial class SelfTests
{
    private static async Task SingBoxJobFixtureAsync(string root)
    {
        var childFolder = Path.Combine(root, "sing-box-child"); Directory.CreateDirectory(childFolder);
        var info = new ProcessStartInfo(TestHost.Executable) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("--job-child"); info.ArgumentList.Add(childFolder); info.ArgumentList.Add("sing-box");
        using var child = Process.Start(info)!;
        var ready = Path.Combine(childFolder, "pid.txt");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (!File.Exists(ready))
            {
                if (child.HasExited) throw new InvalidOperationException("Synthetic controller exited before ready");
                await Task.Delay(100, cancel.Token);
            }
            var pid = int.Parse(await File.ReadAllTextAsync(ready, cancel.Token));
            Assert(IsAlive(pid));
            child.Kill(); await child.WaitForExitAsync(cancel.Token);
            for (var i = 0; i < 30 && IsAlive(pid); i++) await Task.Delay(100, cancel.Token);
            Assert(!IsAlive(pid));
            Assert(!Directory.EnumerateFiles(childFolder, "*.json", SearchOption.AllDirectories).Any());
        }
        finally { if (!child.HasExited) child.Kill(true); }
    }
}
