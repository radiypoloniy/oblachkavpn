using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Все DllImport — системные библиотеки (fwpuclnt, iphlpapi, advapi32, kernel32, winsqlite3):
// искать их только в System32, а не рядом с программой и не в текущей папке. Служба SYSTEM
// иначе загрузила бы подложенную DLL с тем же именем (аудит 19.10, В-2).
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

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
        if (args.Length == 5 && args[0] == "--view-channel-fixture") return ClientViewPreview.Run(args[1], int.Parse(args[2]), args[3], args[4]);
        if (args.Length == 2 && args[0] == "--ui-tray-memory-check") return TrayMemoryPreview.Run(args[1]);
        if (args.Length == 2 && args[0] == "--ui-memory-check") return MemoryPreview.Run(args[1]);
        // Проверки интерфейса работают с частями MainWindow из *Fixtures.cs: они доступны
        // только в сборке с -p:IncludeTestCode=true и отсутствуют в поставляемом клиенте.
        if (args.Length > 1 && args[0] is "--ui-self-test" or "--ui-subscription-self-test")
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            FlagImages.AllowNetwork = false;
            var testApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            testApp.Startup += async (_, _) =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow(persistenceEnabled: false);
                    var checks = args[0] == "--ui-subscription-self-test" ? await window.RunSubscriptionUiFixtureAsync() : await window.RunUiFixtureAsync();
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
        // Ссылка oblachka:// из браузера: работающему окну — через файл и сигнал, новому — при запуске.
        var link = args.FirstOrDefault(DeepLink.IsLink);
        if (!first) { if (link is not null) DeepLink.Hand(link); AppInstance.SignalFirst(); return 0; }
        try
        {
            var app = new Application();
            app.DispatcherUnhandledException += (_, e) =>
            {
                // Don't show Exception.Message: a parser/transport exception could contain credentials.
                CrashLog.Write(e.Exception);
                MessageBox.Show("Приложение завершится из-за внутренней ошибки. Перезапустите его; работа ядра будет остановлена.", ServiceBrand.Name);
                e.Handled = true; app.Shutdown(1);
            };
            using var runtime = new ClientRuntime();
            using var desktop = new ClientDesktop(app, runtime);
            app.Exit += (_, _) => desktop.Close();
            AppInstance.ListenForShow(desktop.ShowFromSignal);
            desktop.Start(args.Contains(AppInstance.TrayArgument), link, args.Contains("--hardware-rendering"));
            return app.Run();
        }
        catch
        { MessageBox.Show("Не удалось открыть приложение. Папка сборки должна быть доступна для записи.", ServiceBrand.Name); return 1; }
    }
}
