using System.IO.Compression;
using System.Text;

namespace Oblachka;

public static partial class SelfTests
{
    private const string FlowsealBat = "@echo off\r\nchcp 65001 > nul\r\nset \"BIN=%~dp0bin\\\"\r\n" +
        "start \"zapret: %~n0\" /min \"%BIN%winws.exe\" --wf-tcp=80,443,%GameFilterTCP% --wf-udp=443,%GameFilterUDP% ^\r\n" +
        "--filter-udp=443 --hostlist=\"%LISTS%list-general.txt\" --hostlist=\"%LISTS%list-general-user.txt\" --dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fake-quic=\"%BIN%quic.bin\" --new ^\r\n" +
        "--filter-tcp=443 --hostlist=\"%LISTS%list-google.txt\" --ip-id=zero --dpi-desync=multisplit --dpi-desync-split-pos=2,sniext+1 --dpi-desync-fake-tls=^! --dpi-desync-fake-tls-mod=rnd,dupsid,sni=www.google.com\r\n";

    private static async Task ZapretChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("zapret: Flowseal .bat becomes a strategy with {BIN}/{LISTS}, escapes and the game filter off", () => Sync(() =>
        {
            var strategy = ZapretStrategy.Parse(ZapretStrategy.NameOf("general (ALT3).bat"), FlowsealBat);
            Assert(strategy.Name == "ALT3" && ZapretStrategy.NameOf("general.bat") == "general");
            Assert(strategy.Args[0] == "--wf-tcp=80,443,12" && strategy.Args.Contains("--hostlist={LISTS}list-general.txt") && strategy.Args.Contains("--dpi-desync-fake-tls=!"));
            Assert(strategy.Args.Contains("--dpi-desync-fake-quic={BIN}quic.bin") && strategy.Args.Count(a => a == "--new") == 1);
            ZapretArgs.Validate(strategy);
            var resolved = ZapretArgs.Resolve(strategy, @"C:\ProgramData\Oblachka\zapret\1\bin", @"C:\ProgramData\Oblachka\zapret\1\lists");
            Assert(resolved.Contains(@"--hostlist=C:\ProgramData\Oblachka\zapret\1\lists\list-general.txt") && resolved.All(a => !a.Contains('{')));
            Assert(ZapretArgs.Files(strategy).OrderBy(f => f).SequenceEqual(new[] { "bin/quic.bin", "lists/list-general-user.txt", "lists/list-general.txt", "lists/list-google.txt" }));
        }));
        await check("zapret: anything beyond bypass options is refused — file writes, paths out of the folder, unknown flags", () => Sync(() =>
        {
            foreach (var bad in new[] { "--debug=@C:\\Windows\\x.txt", "--hostlist=C:\\Windows\\System32\\drivers\\etc\\hosts", "--hostlist={LISTS}..\\..\\x.txt",
                         "--ipset={BIN}../secret", "--dpi-desync-fake-tls=C:\\x.bin", "--daemon", "--pidfile=x", "--wf-tcp=443;calc", "--dpi-desync=fake&calc",
                         "--hostlist-domains=a.com,b c", "--new=1", "--user=nobody" })
                ExpectUserError(() => ZapretArgs.Validate(new("x", new[] { bad })));
            ExpectUserError(() => ZapretArgs.Validate(new("", new[] { "--new" })));
            Assert(ZapretManifest.Rank("ALT2").CompareTo(ZapretManifest.Rank("ALT10")) < 0 && ZapretManifest.Rank("general").CompareTo(ZapretManifest.Rank("ALT")) < 0 &&
                   ZapretManifest.Rank("SIMPLE FAKE ALT2").CompareTo(ZapretManifest.Rank("EXP")) < 0);
        }));
        await check("zapret: manifest is built from a Flowseal archive and refuses foreign URLs and missing files", () => Sync(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "oblachka-zapret-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var zipPath = Path.Combine(folder, "set.zip");
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    void Put(string name, string text) { using var w = new StreamWriter(zip.CreateEntry("set-1.0/" + name).Open(), new UTF8Encoding(false)); w.Write(text); }
                    Put("general.bat", FlowsealBat); Put("general (ALT2).bat", FlowsealBat.Replace("repeats=6", "repeats=8"));
                    foreach (var f in new[] { "bin/winws.exe", "bin/WinDivert.dll", "bin/WinDivert64.sys", "bin/cygwin1.dll", "bin/quic.bin" }) Put(f, f);
                    Put("lists/list-general.txt", "youtube.com\ndiscord.com\n"); Put("lists/list-google.txt", "googlevideo.com\n");
                }
                const string url = ZapretManifest.Source + "1.0/set.zip";
                var manifest = ZapretManifest.FromArchive(zipPath, "1.0", url);
                Assert(manifest.Strategies.Select(s => s.Name).SequenceEqual(new[] { "general", "ALT2" }) && manifest.Root == "set-1.0");
                Assert(manifest.Files.ContainsKey("bin/winws.exe") && manifest.Files.ContainsKey("lists/list-google.txt") && !manifest.Files.ContainsKey("lists/list-general-user.txt"));
                Assert(ZapretManifest.Parse(manifest.ToJson()).Sha256 == manifest.Sha256);
                ExpectUserError(() => ZapretManifest.Parse(manifest.ToJson().Replace(ZapretManifest.Source, "https://evil.invalid/")));
                ExpectUserError(() => (manifest with { Files = manifest.Files.Where(f => f.Key != "bin/quic.bin").ToDictionary(f => f.Key, f => f.Value) }).Validate());
                ExpectUserError(() => (manifest with { Files = new(manifest.Files) { ["bin/../x.exe"] = manifest.Sha256 } }).Validate());
            }
            finally { Directory.Delete(folder, true); }
        }));
        await check("zapret: the shipped manifest (Flowseal set) is valid, and service commands are checked strictly", () => Sync(() =>
        {
            var shipped = ZapretManifest.Embedded();
            Assert(shipped.Strategies.Length >= 10 && shipped.Strategies[0].Name == "general" && shipped.Url.StartsWith(ZapretManifest.Source, StringComparison.Ordinal));
            new ZapretRequest(ZapretAction.Start, "ALT", new[] { "203.0.113.1", "2001:db8::1" }, new[] { "example.org" }).Validate();
            ExpectUserError(() => new ZapretRequest(ZapretAction.Start).Validate());
            ExpectUserError(() => new ZapretRequest(ZapretAction.Stop, "ALT").Validate());
            ExpectUserError(() => new ZapretRequest(ZapretAction.Start, "ALT", new[] { "not-an-ip" }).Validate());
            ExpectUserError(() => new ZapretRequest(ZapretAction.Start, "ALT", null, new[] { "C:\\x" }).Validate());
            var request = new HelperRequest(1, Guid.NewGuid(), HelperOperation.Zapret, null, null, new(ZapretAction.Status));
            using (var wire = new MemoryStream())
            {
                HelperProtocol.WriteAsync(wire, request, CancellationToken.None).GetAwaiter().GetResult();
                wire.Position = 0;
                var parsed = HelperProtocol.ReadAsync<HelperRequest>(wire, CancellationToken.None).GetAwaiter().GetResult();
                HelperProtocol.Validate(parsed);
                Assert(parsed.Zapret!.Action == ZapretAction.Status);
            }
            ExpectUserError(() => HelperProtocol.Validate(request with { Zapret = null }));
            ExpectUserError(() => HelperProtocol.Validate(request with { Operation = HelperOperation.Heartbeat }));
            Assert(ZapretSites.Clean(".YouTube.com # видео") == "youtube.com" && ZapretSites.Clean("1.2.3.4") is null && ZapretSites.Clean("bad domain") is null);
        }));
    }
}
