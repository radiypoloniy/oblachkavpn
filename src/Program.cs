using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RoutePilot;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--prepare-vm-tun")
        {
            try { return VmTunCommand.PrepareAsync(int.Parse(args[1]), args[2], args[3]).GetAwaiter().GetResult(); }
            catch { return 2; }
        }
        if (args.Length == 2 && args[0] == "--architecture-self-test") return SelfTests.RunArchitectureAsync(args[1]).GetAwaiter().GetResult();
        if (args.Length == 1 && args[0] == "--helper-idle-fixture") { Thread.Sleep(30000); return 0; }
        if (args.Length == 4 && args[0] == "--helper-fixture") return SelfTests.HelperFixtureAsync(args[1], int.Parse(args[2]), args[3]).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--lab") return LabCommand.RunAsync(args[1], args[2]).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == "--self-test") return SelfTests.RunAsync(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "self-test.json")).GetAwaiter().GetResult();
        if (args.Length > 1 && args[0] == "--job-child") return SelfTests.JobChildAsync(args[1], args.Length > 2 && args[2] == "sing-box" ? CoreKind.SingBox : CoreKind.Xray).GetAwaiter().GetResult();
        if (args.Length > 1 && args[0] == "--ui-self-test")
        {
            FlagImages.AllowNetwork = false;
            var testApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            testApp.Startup += async (_, _) =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow(persistenceEnabled: false);
                    var checks = await window.RunUiFixtureAsync();
                    File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new { Passed = true, Network = "Only local SOCKS startup; no upstream requests", Checks = checks }));
                    testApp.Shutdown(0);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new { Passed = false, Error = ex.ToString() }));
                    testApp.Shutdown(2);
                }
                finally { window?.Close(); }
            };
            return testApp.Run();
        }
        if (args.Length > 1 && args[0] == "--preview")
            return DesignPreview.Capture(args[1], args.Length > 2 ? args[2] : "welcome", args.Length > 3 && args[3] == "compact");
        using var mutex = new Mutex(true, "Local\\RoutePilot-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToLowerInvariant())))[..16], out var first);
        if (!first) { MessageBox.Show("Эта сборка уже запущена.", "Облачка"); return 1; }
        try
        {
            var app = new Application();
            app.DispatcherUnhandledException += (_, e) =>
            {
                // Don't show Exception.Message: a parser/transport exception could contain credentials.
                MessageBox.Show("Приложение завершится из-за внутренней ошибки. Перезапусти его; работа ядра будет остановлена.", "Облачка");
                e.Handled = true; app.Shutdown(1);
            };
            var window = new MainWindow();
            app.Exit += (_, _) => window.Close();
            return app.Run(window);
        }
        catch
        { MessageBox.Show("Не удалось открыть приложение. Папка сборки должна быть доступна для записи.", "Облачка"); return 1; }
    }
}
