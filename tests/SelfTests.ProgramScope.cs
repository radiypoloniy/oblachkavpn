using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

public static partial class SelfTests
{
    // Правила программ по папкам: что принимается, как попадает в ядро и через службу.
    private static async Task ProgramScopeChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Whole-folder program rules refuse drives, Windows, Program Files and the user profile", () => Sync(() =>
        {
            foreach (var broad in new[] { @"C:\", @"C:\Windows\System32", @"C:\Program Files", @"D:\Program Files (x86)", @"C:\Program Files\Common Files",
                @"C:\Users\Bob", @"C:\Users\Bob\Downloads", @"C:\Users\Bob\AppData\Local", @"C:\Users\Bob\AppData\Local\Programs", @"D:\Games", @"C:\ProgramData" })
                ExpectUserError(() => ProgramRoutingPlan.Create(broad.TrimEnd('\\') + @"\app.exe", SiteAction.Direct, allInFolder: true));
            foreach (var bad in new[] { @"relative\app.exe", @"C:\a:b\app.exe", @"C:\a\..\b\app.exe", @"\\server\share\app.exe", @"C:\a*\app.exe" })
                ExpectUserError(() => ProgramRoutingPlan.Create(bad, SiteAction.Direct, allInFolder: false));
            Assert(ProgramRoutingPlan.Create(@"c:\Program Files (x86)\Steam\steam.exe", SiteAction.Direct, true).Folder == @"C:\Program Files (x86)\Steam");
            Assert(ProgramRoutingPlan.Create(@"C:\Users\Bob\AppData\Local\Discord\Update.exe", SiteAction.Tunnel, true).AllInFolder);
            // Один файл можно указать и в широкой папке — он точный.
            Assert(ProgramRoutingPlan.Create(@"C:\Users\Bob\Downloads\tool.exe", SiteAction.Direct, false).FilePath == @"C:\Users\Bob\Downloads\tool.exe");
            ExpectUserError(() => ProgramRoutingPlan.Validate([new ProgramRule("x.exe", SiteAction.Direct) { AllInFolder = true }]));
            ExpectUserError(() => ProgramRoutingPlan.Validate([new ProgramRule("x.exe", SiteAction.Direct) { Folder = @"C:\Windows", AllInFolder = true }]));
        }));
        await check("Folder rules reach sing-box as escaped path patterns, most specific first, and sing-box accepts them", async () =>
        {
            ProgramRule[] rules =
            [
                ProgramRoutingPlan.Create(@"C:\Program Files (x86)\Steam\steam.exe", SiteAction.Direct, true),
                ProgramRoutingPlan.Create(@"C:\Program Files (x86)\Steam\steamapps\common\Aion 2\Aion2.exe", SiteAction.Tunnel, true),
                ProgramRoutingPlan.Create("curl.exe", SiteAction.Block),
                ProgramRoutingPlan.Create(@"D:\Tools [x]\a+b.exe", SiteAction.Direct, false),
            ];
            var lease = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["192.0.2.1"], Programs: rules);
            await CoreConfigurationCheck.ValidateTunAsync(lease, CancellationToken.None);
            var routes = ((JsonArray)SingBoxTunConfig.Create(lease)["route"]!["rules"]!).Skip(1).Select(r => r!.AsObject()).ToArray();
            string Pattern(int i) => routes[i]["process_path_regex"]![0]!.GetValue<string>();
            Assert(Pattern(0) == @"(?i)^D:\\Tools \[x\]\\a\+b\.exe$" && routes[1]["process_name"]![0]!.GetValue<string>() == "curl.exe");
            Assert(Pattern(2) == @"(?i)^C:\\Program Files \(x86\)\\Steam\\steamapps\\common\\Aion 2\\" && routes[2]["outbound"]!.GetValue<string>() == "local-core");
            Assert(Pattern(3) == @"(?i)^C:\\Program Files \(x86\)\\Steam\\" && routes[3]["outbound"]!.GetValue<string>() == SingBoxTunConfig.UserDirect);
            Assert(SingBoxTunConfig.Create(lease)["route"]!["find_process"]!.GetValue<bool>());
        });
        await check("Folder rules survive the service protocol and old name-only rules still parse", () => Sync(() =>
        {
            var rule = ProgramRoutingPlan.Create(@"C:\Program Files (x86)\Steam\steam.exe", SiteAction.Direct, true);
            var lease = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["192.0.2.1"], Programs: [rule, ProgramRoutingPlan.Create("game.exe", SiteAction.Block)]);
            var options = new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
            var wire = HelperProtocol.Parse<HelperRequest>(JsonSerializer.SerializeToUtf8Bytes(new HelperRequest(1, Guid.NewGuid(), HelperOperation.Acquire, lease), options));
            var validated = NetworkHelperContracts.Validate(wire.Lease!);
            Assert(validated.ProgramRules[0] == rule && validated.ProgramRules[1].Folder is null);
            var old = JsonSerializer.Deserialize<ProgramRule>("{\"Executable\":\"steam.exe\",\"Action\":\"Direct\"}", options)!;
            Assert(old.Folder is null && !old.AllInFolder && old.Key == "steam.exe");
            // Подменённый путь в правиле служба не принимает.
            var forged = lease with { Programs = [rule with { Folder = @"C:\Windows" }] };
            ExpectUserError(() => NetworkHelperContracts.Validate(forged));
        }));
        await check("Steam libraries, Squirrel apps and the app scope pick whole program folders", () => Sync(() =>
        {
            var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}";
            var paths = GameLibraries.Vdf(vdf).Where(p => p.Key == "path").Select(p => p.Value).ToArray();
            Assert(paths.SequenceEqual([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"]));
            Assert(AppRuleScope.Root(@"C:\Users\Bob\AppData\Local\Discord\app-1.0.9213\Discord.exe") == @"C:\Users\Bob\AppData\Local\Discord");
            var steam = new InstalledApp("Steam", "steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", false) { Folders = [@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary\steamapps\common"] };
            var scoped = AppRuleScope.For(steam, SiteAction.Direct);
            Assert(scoped.Length == 2 && scoped.All(r => r.AllInFolder && r.Executable == "steam.exe"));
            // Программа прямо в «Загрузках»: папка слишком общая — правило на один файл.
            var loose = AppRuleScope.For(new InstalledApp("Tool", "tool.exe", @"C:\Users\Bob\Downloads\tool.exe", false), SiteAction.Direct);
            Assert(loose.Single() is { AllInFolder: false, FilePath: @"C:\Users\Bob\Downloads\tool.exe" });
        }));
    }
}
