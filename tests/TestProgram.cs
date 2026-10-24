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
        // Конфигурация ядра для одной ссылки (или файла с ней) тем же кодом, что в приложении:
        // для проверки протокола на локальном сервере. В файле — учётные данные из ссылки.
        // Пятый аргумент --fragment включает «Обход DPI» (TlsFragment), как его включает подбор.
        if (args.Length is 4 or 5 && args[0] == "--link-config")
        {
            var text = File.Exists(args[1]) ? File.ReadAllText(args[1]) : args[1];
            var profile = Subscriptions.Parse(text).Profiles[0];
            var adapter = new CoreAdapter(profile.Core, Path.Combine(AppContext.BaseDirectory, "tools", profile.Core == CoreKind.Xray ? "xray.exe" : "sing-box.exe"));
            var config = adapter.RuntimeConfig(profile.Outbound, int.Parse(args[3]));
            var cut = args.Length == 5 && args[4] == "--fragment" && TlsFragment.Apply(config, profile.Core);
            File.WriteAllText(args[2], config.ToJsonString());
            Console.WriteLine(profile.Core + " | " + profile.Transport + (cut ? " | fragment" : ""));
            return 0;
        }
        // Манифест zapret из архива Flowseal (задача обновления): архив, тег, адрес, куда писать.
        if (args.Length == 5 && args[0] == "--zapret-manifest")
        {
            var manifest = ZapretManifest.FromArchive(args[1], args[2], args[3]);
            File.WriteAllText(args[4], manifest.ToJson());
            Console.WriteLine(manifest.Tag + ": стратегий " + manifest.Strategies.Length + ", файлов " + manifest.Files.Count);
            return 0;
        }
        // Команда zapret установленной службе (стенд VM): Prepare | Start <стратегия> | Status | Stop.
        // Отчёт: состояние службы и процессы winws (путь, сессия) — без адресов и ключей.
        if (args.Length is 3 or 4 && args[0] == "--zapret-call")
        {
            var action = Enum.Parse<ZapretAction>(args[2]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var lifetime = ServiceHelperLifetime.ConnectAsync(NetworkServiceControl.Query().ProcessId, timeout.Token).GetAwaiter().GetResult();
            ZapretStatus status;
            try
            {
                var exclude = action == ZapretAction.Start ? new[] { "203.0.113.7" } : null;
                status = lifetime.Client.ZapretAsync(new(action, args.Length == 4 ? args[3] : null, exclude), timeout.Token).GetAwaiter().GetResult();
            }
            finally { lifetime.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            var processes = System.Diagnostics.Process.GetProcessesByName("winws")
                .Select(p => (HelperPipeIdentity.ImagePath(p) ?? "?") + " | сессия " + p.SessionId + " | наш " + ZapretRunner.IsOurs(p)).ToArray();
            File.WriteAllText(args[1], System.Text.Json.JsonSerializer.Serialize(new { status.Ready, status.Running, status.Tag, status.Strategy, Strategies = status.Strategies.Length, status.Error, Winws = processes }));
            return 0;
        }
        // Сохранённое состояние для стенда: одно подключение во «Вручную», режим «Браузеры»,
        // чтобы окно открылось на главном экране и ничего не подключало само.
        if (args.Length == 2 && args[0] == "--lab-state")
        {
            var lab = Profiles.Parse(args[1]);
            var saved = new SavedProfile(lab.Name, (System.Text.Json.Nodes.JsonObject)lab.Outbound.DeepClone()) { Source = ProfileSource.External, Group = ProfileGroup.ManualId };
            new ClientStateStore(ClientDataPaths.StateFile).Save(new ClientSnapshot(1, new[] { saved }, null, Array.Empty<string>(), 0, true, true)
                { SystemTunnel = false, SystemProxy = false, Groups = new[] { ProfileGroup.Manual } });
            return 0;
        }
        // Диагноз отказа (ConnectionDiagnosis) для ссылки: вид и пояснение.
        if (args.Length == 3 && args[0] == "--diagnose")
        {
            var text = File.Exists(args[1]) ? File.ReadAllText(args[1]) : args[1];
            var diagnosis = ConnectionDiagnosis.RunAsync(Subscriptions.Parse(text).Profiles[0], CancellationToken.None).GetAwaiter().GetResult();
            File.WriteAllText(args[2], diagnosis.Kind + " | " + diagnosis.Explanation);
            return 0;
        }
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
        // «Проверить сеть Windows» на этом компьютере — только чтение.
        if (args.Length == 2 && args[0] == "--network-doctor")
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var found = NetworkDoctor.Diagnose(new WindowsNetworkProbe());
            File.WriteAllLines(args[1], found.Select(f => f.Action.Kind + " | " + f.Title + " | " + f.Text).Append($"за {clock.ElapsedMilliseconds} мс"));
            return 0;
        }
        // Разбор сохранённого ответа подписки: только число узлов и их имена, без ключей.
        if (args.Length == 3 && args[0] == "--parse-file")
        {
            string report;
            try
            {
                var result = Subscriptions.Parse(File.ReadAllText(args[1]));
                report = $"узлов {result.Profiles.Count}, неподдерживаемых {result.Unsupported}, некорректных {result.Invalid}: " +
                    string.Join(" | ", result.Profiles.Select(p => p.DisplayName + " (" + p.Core + " " + p.Transport + ")"));
            }
            catch (UserError ex) { report = "ошибка: " + ex.Message; }
            File.WriteAllText(args[2], report);
            return 0;
        }
        // Совпадают ли узлы двух ответов подписки (по отпечатку конфигурации), без вывода ключей.
        if (args.Length == 4 && args[0] == "--compare-files")
        {
            var a = Subscriptions.Parse(File.ReadAllText(args[1])).Profiles;
            var b = Subscriptions.Parse(File.ReadAllText(args[2])).Profiles;
            var lines = a.Select(p => (p.DisplayName, match: b.Any(q => Profiles.Fingerprint(q) == Profiles.Fingerprint(p)))).Select(x => x.DisplayName + ": " + (x.match ? "совпадает" : "ОТЛИЧАЕТСЯ")).ToList();
            foreach (var p in a.Where(p => !b.Any(q => Profiles.Fingerprint(q) == Profiles.Fingerprint(p))))
            {
                var twin = b.FirstOrDefault(q => q.Name == p.Name && q.Transport == p.Transport);
                if (twin is null) continue;
                var keysA = Flatten(p.Outbound); var keysB = Flatten(twin.Outbound);
                lines.Add("  " + p.DisplayName + " расходится в: " + string.Join(", ", keysA.Keys.Union(keysB.Keys).Where(k => keysA.GetValueOrDefault(k) != keysB.GetValueOrDefault(k)).Take(8)));
            }
            File.WriteAllLines(args[3], lines);
            return 0;
        }
        // Какие группы получатся из файла состояния (только чтение): имена и число узлов, без ключей.
        if (args.Length == 3 && args[0] == "--groups-from-state")
        {
            var snapshot = new ClientStateStore(args[1]).Load()!;
            var restored = ClientStateStore.RestoreProfiles(snapshot);
            var groups = snapshot.Groups ?? ProfileGroups.FromLegacy(snapshot, restored).ToArray();
            File.WriteAllLines(args[2], groups.Select(g => g.Id + " | " + g.Name + " | адресов " + g.Sources.Length + " | узлов " +
                restored.Count(p => snapshot.Groups is null ? p.IsService == (g.Source == ProfileSource.Service) : p.Group == g.Id)));
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
        if (args.Length == 2 && args[0] == "--wfp-local-check") return WfpLocalCheckCommand.Run(args[1]);
        if (args.Length == 3 && args[0] == "--ping-check") return PingCheckCommand.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--throughput-check" && int.TryParse(args[1], out var socks)) return ThroughputCheckCommand.Run(socks, args[2]);
        if (args.Length >= 3 && args[0] == "--site-check") return SiteCheckCommand.Run(args[1], args.Skip(2));
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

    private static Dictionary<string, string> Flatten(System.Text.Json.Nodes.JsonNode? node, string prefix = "")
    {
        var result = new Dictionary<string, string>();
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject o: foreach (var (k, v) in o) foreach (var x in Flatten(v, prefix + "." + k)) result[x.Key] = x.Value; break;
            case System.Text.Json.Nodes.JsonArray a: for (var i = 0; i < a.Count; i++) foreach (var x in Flatten(a[i], prefix + "[" + i + "]")) result[x.Key] = x.Value; break;
            case null: result[prefix] = "null"; break;
            default: result[prefix] = node.ToJsonString().Length.ToString(); break;
        }
        return result;
    }
}
