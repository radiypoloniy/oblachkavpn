using System.Diagnostics;

namespace Oblachka;

// Брандмауэр Windows для прокси локальной сети (TunnelTuning.AddLan). Ядро туннеля запускает
// служба, и входящие к нему Windows молча отклоняет — запроса, как у программ пользователя, не
// будет. Пока туннель с прокси работает, служба держит правило: только это ядро, только этот
// порт и только локальная подсеть. С остановкой туннеля правило убирается.
internal static class LanFirewall
{
    private static readonly string RuleName = ServiceBrand.Name + " — прокси для локальной сети";
    private static bool open;

    public static void Open(string executable, int port)
    {
        Close(force: true);
        foreach (var protocol in new[] { "TCP", "UDP" })
            Run("add", "rule", "name=" + RuleName, "dir=in", "action=allow", "program=" + executable, "protocol=" + protocol,
                "localport=" + port, "remoteip=localsubnet", "profile=any", "edge=no");
        open = true;
    }

    // Без прокси правило снимается, только если его ставила эта служба: netsh на каждом
    // подключении стоил бы десятые доли секунды.
    public static void Close(bool force = false)
    {
        if (!open && !force) return;
        Run("delete", "rule", "name=" + RuleName);
        open = false;
    }

    private static void Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "advfirewall", "firewall" }.Concat(arguments)) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000)) { process.Kill(); return; }
            if (arguments[0] == "add" && process.ExitCode != 0)
                NetworkHelperHost.RecordStep(DateTime.UtcNow.ToString("HH:mm:ss.fff") + " брандмауэр: правило для локальной сети не добавлено, код " + process.ExitCode);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { NetworkHelperHost.RecordStep(DateTime.UtcNow.ToString("HH:mm:ss.fff") + " брандмауэр недоступен: " + ex.GetType().Name); }
    }
}
