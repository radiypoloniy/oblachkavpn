using System.Diagnostics;

namespace Oblachka;

// Живая проверка защиты WFP с «Локальной сетью» (аудит 19.10, В-1). Только на стенде, от администратора:
//   Oblachka.Tests.exe --wfp-local-check <файл отчёта>
// Ставит защиту с local=1, где ядром туннеля притворяется копия curl.exe, и пробует соединения:
// ядро → частный адрес (должно пройти до сокета), ядро → публичный адрес и другая программа →
// частный адрес (должны упереться в WFP). Потом защиту снимает.
internal static class WfpLocalCheckCommand
{
    internal static int Run(string reportPath)
    {
        var lines = new List<string>();
        try { return Check(lines); }
        catch (Exception ex) { lines.Add("Сбой: " + ex); return 1; }
        finally { File.WriteAllLines(reportPath, lines); }
    }

    private static int Check(List<string> lines)
    {
        var tools = Path.Combine(Path.GetTempPath(), "oblachka-wfp-local", "tools");
        Directory.CreateDirectory(tools);
        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        foreach (var name in new[] { TunCore.FileName, "xray.exe", "sing-box.exe" }) File.Copy(curl, Path.Combine(tools, name), true);
        var backend = new WfpProtectionBackend(WfpEngine.Open, tools);
        var lease = NetworkHelperContracts.Validate(new(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, new[] { "203.0.113.7" }, LocalNetwork: true));
        var change = backend.Prepare(lease)[0];
        lines.Add("Изменение: " + change.After);
        try
        {
            backend.Apply(lease.LeaseId, change);
            lines.Add("Защита поставлена.");
            lines.Add("ядро → 10.0.2.2:1: " + Curl(Path.Combine(tools, TunCore.FileName), "http://10.0.2.2:1/"));
            lines.Add("ядро → 1.1.1.1:443: " + Curl(Path.Combine(tools, TunCore.FileName), "https://1.1.1.1/"));
            lines.Add("curl → 10.0.2.2:1: " + Curl(curl, "http://10.0.2.2:1/"));
        }
        catch (Exception ex) { lines.Add("Ошибка: " + ex.Message); }
        finally
        {
            try { backend.Restore(lease.LeaseId, change); lines.Add("Защита снята."); }
            catch (Exception ex) { lines.Add("Снятие: " + ex.Message); }
            lines.Add("после снятия, curl → 1.1.1.1:443: " + Curl(curl, "https://1.1.1.1/"));
        }
        return 0;
    }

    private static string Curl(string exe, string url)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, "-sS -o NUL -m 6 -w \"code=%{http_code}\" " + url)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
        var output = process.StandardOutput.ReadToEnd() + " " + process.StandardError.ReadToEnd();
        process.WaitForExit(10000);
        return "выход " + process.ExitCode + ": " + output.Trim().ReplaceLineEndings(" ");
    }
}
