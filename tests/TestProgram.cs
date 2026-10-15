namespace Oblachka;

// Точка входа тестового исполняемого файла. Поставляемый клиент этих аргументов не знает.
public static class TestProgram
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--architecture-self-test")
            return SelfTests.RunArchitectureAsync(args[1]).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--owner-fixture") return SystemTunnelOwnerLoss.Fixture(args[1], args[2]);
        if (args.Length == 1 && args[0] == "--helper-idle-fixture") { Thread.Sleep(30000); return 0; }
        if (args.Length == 4 && args[0] == "--helper-fixture")
            return SelfTests.HelperFixtureAsync(args[1], int.Parse(args[2]), args[3]).GetAwaiter().GetResult();
        if (args.Length is 5 or 6 && args[0] == "--system-tunnel-check")
            return SystemTunnelCheckCommand.Run(args[1], args[2], args[3], int.Parse(args[4]), args.Length == 6 && args[5] == "--real-machine");
        if (args.Length == 5 && args[0] == "--export-core-config")
            return ExportCoreConfigCommand.Run(args[1], args[2], args[3], int.Parse(args[4]));
        if (args.Length == 4 && args[0] == "--local-rules-check") return LocalRulesCheckCommand.Run(args[1], args[2], args[3]);
        // Подписки других клиентов на этом компьютере: клиент, имя и хост, без токенов.
        // Третий аргумент — подменная папка: в ней ищутся все хранилища (AppData, LocalAppData…).
        if (args.Length is 2 or 3 && args[0] == "--foreign-clients")
        {
            var roots = args.Length == 3 ? new ForeignClients.Roots(args[2], args[2], args[2], args[2]) : null;
            File.WriteAllLines(args[1], ForeignClients.Find(roots).Select(f => f.Client + " | " + f.Name + " | " + f.Host));
            return 0;
        }
        // Каталог программ этого компьютера, как его видит выбор программ в правилах.
        if (args.Length == 2 && args[0] == "--installed-apps")
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var apps = InstalledApps.Find();
            var lines = apps.Select(a => a.Name + " | " + a.Executable + (a.Running ? " | запущена" : "")).ToList();
            lines.Insert(0, $"Программ {apps.Count}, за {clock.ElapsedMilliseconds} мс; поиск «тел»: " + string.Join(", ", InstalledApps.Search(apps, "тел").Take(3).Select(a => a.Name)));
            File.WriteAllLines(args[1], lines);
            return 0;
        }
        // Распознавание QR на картинках: для каждого файла — текст или «не распознан».
        if (args.Length >= 3 && args[0] == "--qr-decode")
        {
            var lines = new List<string>();
            foreach (var file in args.Skip(2))
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using var bitmap = new System.Drawing.Bitmap(file);
                lines.Add(Path.GetFileName(file) + "	" + (QrImages.Decode(bitmap) ?? "<не распознан>") + "	" + clock.ElapsedMilliseconds + " мс");
            }
            File.WriteAllLines(args[1], lines);
            return 0;
        }
        // Сканирование настоящих экранов, как кнопка «Сканировать QR-код на экране».
        if (args.Length == 2 && args[0] == "--qr-screen")
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            File.WriteAllText(args[1], (QrImages.ScanScreens() ?? "<не найден>") + "	" + clock.ElapsedMilliseconds + " мс");
            return 0;
        }
        if (args.Length == 3 && args[0] == "--qr-debug")
        {
            using var bitmap = new System.Drawing.Bitmap(args[2]);
            var (gray, w, h) = QrImages.Gray(bitmap);
            var lines = new List<string>();
            foreach (var grid in QrDetector.Grids(gray, w, h))
            {
                var size = grid.GetLength(0);
                string? text = null; string error = "";
                try { text = QrDecoder.Read(grid); } catch (Exception ex) { error = ex.GetType().Name; }
                lines.Add($"size {size}: {(text is null ? "нет " + error : "OK")}");
            }
            File.WriteAllLines(args[1], lines);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--routing-check") return RoutingCheckCommand.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--ping-check") return PingCheckCommand.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--subscription-check") return SubscriptionCheckCommand.Run(args[1], args[2]);
        if (args.Length is 3 or 4 && args[0] == "--import-check")
            return ImportCheckCommand.Run(args[1], args[2], args.Length > 3 && args[3] == "--validate");
        if (args.Length == 3 && args[0] == "--lab")
            return LabCommand.RunAsync(args[1], args[2]).GetAwaiter().GetResult();
        if (args.Length > 1 && args[0] == "--job-child")
            return SelfTests.JobChildAsync(args[1], args.Length > 2 && args[2] == "sing-box" ? CoreKind.SingBox : CoreKind.Xray).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == "--self-test")
            return SelfTests.RunAsync(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "self-test.json")).GetAwaiter().GetResult();
        Console.Error.WriteLine("Oblachka.Tests: нужен один из аргументов --self-test, --architecture-self-test, --lab.");
        return 1;
    }
}
