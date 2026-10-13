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

    // Пакет с адресом туннеля в отправителе на адрес узла. Узел выведен из туннеля
    // маршрутом, значит пакет пойдёт на физический адаптер, а слой соединений его
    // пропускает: адрес отправителя — туннельный. Закрывает это только фильтр пакетов.
    // В захвате снаружи таких кадров быть не должно; полезная нагрузка — метка поиска.
    internal static string TunnelSourceToPeer(string peer)
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
            socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Parse(SingBoxTunConfig.TunHost4), 0));
            var marker = System.Text.Encoding.ASCII.GetBytes("oblachka-tunnel-source-probe");
            for (var i = 0; i < 5; i++) socket.SendTo(marker, new System.Net.IPEndPoint(System.Net.IPAddress.Parse(peer), 443));
            return "адрес туннеля к узлу: 5 пакетов отправлено, в захвате их быть не должно (метка oblachka-tunnel-source-probe)";
        }
        catch (Exception ex) { return "адрес туннеля к узлу: отправка отклонена (" + ex.GetType().Name + ")"; }
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
