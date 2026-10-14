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
        // Сетевая служба и её установка (установщик, права администратора).
        if (args.Length == 1 && args[0] == NetworkService.Argument) return NetworkService.Run();
        if (args.Length == 1 && args[0] == NetworkServiceControl.InstallArgument) return NetworkServiceControl.Install();
        if (args.Length == 1 && args[0] == NetworkServiceControl.UninstallArgument) return NetworkServiceControl.Uninstall();
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
        using var mutex = new Mutex(true, AppInstance.MutexName, out var first);
        // Уже запущена (например, свёрнута в трей): показываем её окно вместо ошибки.
        if (!first) { AppInstance.SignalFirst(); return 0; }
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
            if (args.Contains(AppInstance.TrayArgument)) window.StartHiddenInTray();
            app.Exit += (_, _) => window.Close();
            AppInstance.ListenForShow(window.ShowFromSignal);
            return app.Run(window);
        }
        catch
        { MessageBox.Show("Не удалось открыть приложение. Папка сборки должна быть доступна для записи.", ServiceBrand.Name); return 1; }
    }
}
