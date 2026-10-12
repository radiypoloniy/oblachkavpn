using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Oblachka;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Привилегированная половина системного режима. Запускается самим приложением
        // с явным повышением прав; окно продолжает работать без прав администратора.
        if (args.Length == 3 && args[0] == NetworkHelperHost.Argument) return NetworkHelperHost.Run(args);
        if (args.Length == 2 && args[0] == "--vm-tun-host") return VmTunWindow.Run(args[1]);
        if (args.Length == 4 && args[0] == "--prepare-vm-tun")
        {
            try { return VmTunCommand.PrepareAsync(int.Parse(args[1]), args[2], args[3]).GetAwaiter().GetResult(); }
            catch { return 2; }
        }
#if TEST_HOOKS
        // Проверки интерфейса работают с частями MainWindow из *Fixtures.cs: они доступны
        // только в сборке с -p:IncludeTestCode=true и отсутствуют в поставляемом клиенте.
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
#endif
        using var mutex = new Mutex(true, "Local\\Oblachka-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToLowerInvariant())))[..16], out var first);
        if (!first) { MessageBox.Show("Эта сборка уже запущена.", ServiceBrand.Name); return 1; }
        try
        {
            var app = new Application();
            app.DispatcherUnhandledException += (_, e) =>
            {
                // Don't show Exception.Message: a parser/transport exception could contain credentials.
                MessageBox.Show("Приложение завершится из-за внутренней ошибки. Перезапустите его; работа ядра будет остановлена.", ServiceBrand.Name);
                e.Handled = true; app.Shutdown(1);
            };
            var window = new MainWindow();
            app.Exit += (_, _) => window.Close();
            return app.Run(window);
        }
        catch
        { MessageBox.Show("Не удалось открыть приложение. Папка сборки должна быть доступна для записи.", ServiceBrand.Name); return 1; }
    }
}
