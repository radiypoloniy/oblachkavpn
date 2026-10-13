using System.Net.Sockets;

namespace Oblachka;

// Наблюдение за блокировкой трафика мимо туннеля на стенде.
//
// Главный вопрос, который здесь решается: обрывает ли блокировка соединения, открытые до
// включения режима. Захват трафика снаружи гостя показал, что именно их кадры уходили
// мимо туннеля, пока он работал. Брандмауэр Windows принимает решение при установлении
// соединения, поэтому заранее неизвестно, пересматривает ли он уже открытые — ответ даёт
// только опыт, а не рассуждение.
internal static class KillSwitchProbe
{
    // Состояние брандмауэра читается тем же путём, которым его меняет помощник.
    internal static string Describe()
    {
        try
        {
            var policy = WindowsFirewallPolicy.Open();
            var parts = WindowsFirewallPolicy.AllProfiles.Select(p =>
                p + ": " + (policy.IsEnabled(p) ? "вкл" : "выкл") + "/" + policy.OutboundAction(p));
            var rules = KillSwitchBackend.RuleNames.Where(policy.RuleExists).Count();
            return string.Join(", ", parts) + "; наших разрешений: " + rules;
        }
        catch (Exception ex) { return "прочитать не удалось (" + ex.GetType().Name + ")"; }
    }

    // Соединение, открытое до туннеля, и оставленное живым.
    internal static TcpClient? OpenBefore(string host, int port, List<string> log)
    {
        try
        {
            var client = new TcpClient();
            client.Connect(host, port);
            log.Add("соединение до туннеля открыто: " + host + ":" + port);
            return client;
        }
        catch (Exception ex)
        {
            log.Add("соединение до туннеля не открылось (" + ex.GetType().Name + ") — вопрос этим прогоном не решается");
            return null;
        }
    }

    // Попытка воспользоваться тем же соединением при поднятом туннеле. Запрос идёт
    // открытым текстом: нас интересует сам факт прохождения байтов, а не содержимое.
    internal static string UseAfter(TcpClient? client, string host)
    {
        if (client is null) return "нечего проверять: соединение не открывалось";
        try
        {
            var request = "GET / HTTP/1.0\r\nHost: " + host + "\r\nConnection: close\r\n\r\n";
            var stream = client.GetStream();
            stream.WriteTimeout = 10000;
            stream.ReadTimeout = 10000;
            var bytes = System.Text.Encoding.ASCII.GetBytes(request);
            stream.Write(bytes, 0, bytes.Length);
            var buffer = new byte[64];
            var read = stream.Read(buffer, 0, buffer.Length);
            return read > 0
                ? "ОТКРЫТОЕ РАНЬШЕ СОЕДИНЕНИЕ РАБОТАЕТ: получено " + read + " байт мимо туннеля — блокировка его не обрывает"
                : "открытое раньше соединение закрыто без данных — блокировка его обрывает";
        }
        catch (Exception ex)
        {
            return "открытое раньше соединение не прошло (" + ex.GetType().Name + ") — блокировка его обрывает";
        }
    }

    // Новое соединение при поднятом туннеле должно идти через туннель, а не напрямую.
    // Проверяется отдельно от выходного IP: тут важен сам отказ при обходе.
    internal static string NewDirect(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connected = client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(10));
            return connected
                ? "новое соединение с " + host + ":" + port + " установилось (через туннель или мимо — смотрите захват)"
                : "новое соединение с " + host + ":" + port + " не установилось за 10 с";
        }
        catch (Exception ex) { return "новое соединение отказано (" + ex.GetType().Name + ")"; }
    }
}
