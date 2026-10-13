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

    // Включённые разрешения для исходящего трафика, кроме наших. Каждое из них сильнее
    // запрета по умолчанию, то есть потенциально выпускает свою программу мимо туннеля.
    // «Широкие» — без привязки к программе, пакету и адресу: такие пропускают кого угодно.
    // Имена правил — из Windows и установленных программ, ключей и адресов в них нет.
    internal static string ForeignAllows()
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
            var policy = Activator.CreateInstance(type)!;
            var rules = (System.Collections.IEnumerable)Get(policy, "Rules")!;
            var ours = new HashSet<string>(KillSwitchBackend.RuleNames, StringComparer.Ordinal);
            int total = 0, programs = 0; var wide = new List<string>();
            foreach (var rule in rules)
            {
                if (Convert.ToInt32(Get(rule, "Direction")) != 2 || Convert.ToInt32(Get(rule, "Action")) != 1 ||
                    !Convert.ToBoolean(Get(rule, "Enabled"))) continue;
                var name = Convert.ToString(Get(rule, "Name")) ?? "";
                if (ours.Contains(name)) continue;
                total++;
                var program = Convert.ToString(Get(rule, "ApplicationName"));
                var service = Convert.ToString(Get(rule, "ServiceName"));
                var remote = Convert.ToString(Get(rule, "RemoteAddresses"));
                // Владелец задан у правил приложений Store: они действуют только на свой пакет.
                var owner = Convert.ToString(Get(rule, "LocalUserOwner"));
                if (!string.IsNullOrEmpty(program) || !string.IsNullOrEmpty(service) || !string.IsNullOrEmpty(owner)) programs++;
                else if (string.IsNullOrEmpty(remote) || remote == "*") wide.Add(name);
            }
            return "чужих включённых разрешений: " + total + ", из них привязаны к программе, службе или пакету: " + programs +
                ", без программы и на любой адрес: " + wide.Count +
                (wide.Count > 0 ? " (" + string.Join("; ", wide.Distinct().Take(12)) + ")" : "");
        }
        catch (Exception ex) { return "разрешения прочитать не удалось (" + ex.GetType().Name + ")"; }

        static object? Get(object target, string name) =>
            target.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, target, null);
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

    // Разрешение имени и соединение отказывают одинаково — через HttpRequestException.
    // Чтобы различать их, имя разрешается отдельно.
    internal static string Resolve(string host)
    {
        try
        {
            var addresses = System.Net.Dns.GetHostAddresses(host);
            return addresses.Length == 0
                ? "имя " + host + " не разрешено: пустой ответ"
                : "имя " + host + " разрешено в " + addresses[0];
        }
        catch (Exception ex) { return "имя " + host + " не разрешено (" + ex.GetType().Name + ")"; }
    }

    // Какие резолверы видит Windows. При поднятом туннеле запросы обязаны уходить в него,
    // а не на резолвер физической сети.
    internal static string Resolvers()
    {
        try
        {
            var lines = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .Select(n => n.Name + ": " + string.Join('/', n.GetIPProperties().DnsAddresses.Select(a => a.ToString())));
            return string.Join(" | ", lines);
        }
        catch (Exception ex) { return "прочитать не удалось (" + ex.GetType().Name + ")"; }
    }

    // Лабораторный приём: свои запреты DNS снимаются на живом туннеле, и разрешение имени
    // пробуется снова. Это различает две причины, которые снаружи выглядят одинаково, —
    // запрет DNS и запрет исходящего трафика по умолчанию. Снятие безопасно: сессия при
    // остановке снимает правила заново и проверяет, что их не осталось.
    internal static string ResolveWithoutDnsBlock(string host)
    {
        try
        {
            var policy = WindowsFirewallPolicy.Open();
            policy.RemoveRule(KillSwitchBackend.DnsRule);
            policy.RemoveRule(KillSwitchBackend.DnsTcpRule);
            return "после снятия наших запретов DNS: " + Resolve(host);
        }
        catch (Exception ex) { return "снять запреты DNS не удалось (" + ex.GetType().Name + ")"; }
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
