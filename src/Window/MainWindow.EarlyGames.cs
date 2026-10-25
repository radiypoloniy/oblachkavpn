namespace Oblachka;

// Игры, запущенные до включения «Весь компьютер». Их подключённые сокеты остаются на прежнем
// пути, мимо туннеля, и защита их режет: игра уходит в «автономный режим», пока её не
// перезапустят (Dota 2, 25.10.2026). Программы «напрямую» защита пропускает (WfpDirectApps),
// а про остальные игры из библиотек Steam и Epic окно говорит сразу после подключения.
public partial class MainWindow
{
    private async Task WarnEarlyGamesAsync()
    {
        var rules = programRules.ToArray();
        string[] names;
        try
        {
            names = await Task.Run(() =>
            {
                var running = WfpDirectApps.Running();
                // Сам Steam (клиент) переподключается сам — предупреждать есть смысл только об играх.
                return GameLibraries.Steam().Concat(GameLibraries.Epic()).Where(g => g.Name != "Steam")
                    .Where(g => running.Any(p => g.Folders.Any(f => p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase)) &&
                        ProgramRoutingPlan.Find(rules, p)?.Action != SiteAction.Direct))
                    .Select(g => g.Name).Distinct().Take(3).ToArray();
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return; }
        if (names.Length == 0 || closed || !SystemTunnelActive) return;
        DetailText.Text = EarlyGamesText(names);
    }

    internal static string EarlyGamesText(IReadOnlyList<string> names) =>
        (names.Count == 1 ? names[0] + " запущена" : string.Join(", ", names) + " запущены") +
        " до VPN: соединения игры оборвутся. Перезапустите её или отправьте напрямую в «Программах и сайтах».";
}
