using System.Diagnostics;

namespace Oblachka;

// Пробивает ли чужое разрешение блокировку. Адрес узла выведен из туннеля маршрутом,
// поэтому соединение с ним идёт по физической сети и решается только брандмауэром.
// Посторонняя программа (curl из Windows) пробует достучаться до узла дважды: без
// правила — обязана получить отказ, иначе разрешение узла открыто не только ядрам; с
// разрешением для curl — проверяется, что такое правило сильнее нашей блокировки.
// Это та же механика, которой выходят мимо туннеля программы с собственными
// разрешениями на краях окна, когда туннеля ещё или уже нет.
internal static class ForeignAllowProbe
{
    private const string RuleName = "lab-allow-curl";

    internal static void Run(string peer, Action<string> add)
    {
        var curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        if (!File.Exists(curl)) { add("чужое разрешение: curl не найден, опыт пропущен"); return; }
        add("чужое разрешение: curl к узлу без правила — " + Connect(curl, peer));
        try
        {
            var policy = WindowsFirewallPolicy.Open();
            policy.AddRule(new FirewallRule(RuleName, Program: curl));
            add("чужое разрешение: curl к узлу с разрешением для curl — " + Connect(curl, peer));
            policy.RemoveRule(RuleName);
        }
        catch (Exception ex) { add("чужое разрешение: правило не поставлено (" + ex.GetType().Name + ")"); }
    }

    // Код 7 и 28 — соединение не установлено. Любой другой код (в том числе ошибка TLS)
    // значит, что TCP-соединение состоялось.
    private static string Connect(string curl, string peer)
    {
        var info = new ProcessStartInfo(curl) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-s", "-k", "-o", "NUL", "--connect-timeout", "6", "-m", "10", "https://" + peer + "/" }) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return p.ExitCode is 7 or 28 ? "НЕ соединился (код " + p.ExitCode + ")" : "СОЕДИНИЛСЯ (код " + p.ExitCode + ")";
    }
}
