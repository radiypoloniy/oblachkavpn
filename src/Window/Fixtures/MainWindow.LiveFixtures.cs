using System.Windows;
using System.Windows.Input;

namespace Oblachka;

// Превью «Сейчас в сети» (--preview <png> live): подложные программы с файлами, сайтами и строкой,
// которая прямо сейчас переключается. Таблицы соединений компьютера не читаются.
public partial class MainWindow
{
    private void LivePreview()
    {
        // «Сейчас в сети» с подложными программами: таблицы соединений компьютера не читаются.
        LoadGroupsPreview(); Navigate("Connection");
        Live.Show(false, "Правило действует сразу — для новых соединений программы.");
        var steam = new Views.ConnectionRow(@"C:\Program Files (x86)\Steam\steam.exe") { Name = "Steam", Summary = "23 соединения", Route = "через VPN", RouteKind = "vpn", Vpn = 23,
            Remotes = "23.62.99.12:443 · 155.133.252.38:27019", Scope = "Правило возьмёт Steam целиком · 3 папки — со всеми службами и помощниками.",
            Folders = [@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary", @"E:\Games\Steam"], Expanded = true, FoldersShown = false };
        // Только что нажали «VPN»: плашка уже показывает новый путь, соединения переезжают.
        static Views.ConnectionRow Switching(Views.ConnectionRow row) { row.SwitchTo(SiteAction.Tunnel); return row; }
        Views.FileRow File(string path, int vpn, int direct, string speed, SiteAction? rule = null)
        {
            var file = new Views.FileRow(path) { Name = Path.GetFileName(path), Rule = rule };
            file.ShowRoute(vpn, direct);
            file.Summary = string.Join(" · ", new[] { Views.LiveRouteRow.Count(vpn + direct), file.Route, speed, rule is null ? "" : "своё правило" }.Where(p => p.Length > 0));
            return file;
        }
        Views.SiteRow Site(string name, int vpn, int direct, string speed, bool ids = true)
        {
            var site = new Views.SiteRow(name) { Ids = ids ? ["x"] : [] };
            site.ShowRoute(vpn, direct);
            site.Detail = string.Join(" · ", new[] { vpn + direct > 1 ? "×" + (vpn + direct) : "", site.Route, speed }.Where(p => p.Length > 0));
            return site;
        }
        steam.Files = [File(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", 3, 0, "1,4 МБ/с", SiteAction.Tunnel),
            File(@"C:\Program Files (x86)\Steam\steam.exe", 0, 12, "220 КБ/с"), File(@"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe", 0, 8, "")];
        steam.Sites = [Site("steamcontent.com", 0, 6, "4,8 МБ/с"), Site("155.133.252.38", 3, 0, "1,4 МБ/с"), Site("steamcommunity.com", 0, 4, ""), Site("steampowered.com", 0, 2, "")];
        // Раскрытый сайт: правило только в Steam и для всех программ.
        steam.Sites[0].Domain = "steamcontent.com"; steam.Sites[0].AppConfigurable = true; steam.Sites[0].AppLabel = "Только в Steam";
        steam.Sites[0].AppRule = SiteAction.Direct; steam.Sites[0].Expanded = true;
        steam.Summary = "23 соединения · 6,4 МБ/с";
        Live.Load([new Views.ConnectionRow(@"C:\Apps\Telegram\Telegram.exe") { Name = "Telegram Desktop", Summary = "8 соединений", Route = "напрямую", RouteKind = "direct", Direct = 8, Rule = SiteAction.Direct, RuleText = "напрямую" },
            steam, Switching(new Views.ConnectionRow(@"C:\Apps\Chrome\chrome.exe") { Name = "Google Chrome", Summary = "33 соединения · 2,1 МБ/с", Vpn = 31, Direct = 2 }),
            new Views.ConnectionRow(@"D:\Games\Aion2\Aion2.exe") { Name = "AION 2", Summary = "3 соединения", Route = "через VPN", RouteKind = "vpn", Vpn = 3, Rule = SiteAction.Tunnel, RuleText = "через VPN" },
            new Views.ConnectionRow(@"C:\Windows\System32\svchost.exe") { Name = "Хост-процесс для служб Windows", Summary = "2 соединения", Route = "напрямую", RouteKind = "direct", Direct = 2, Configurable = false }]);
    }
}
