using System.Text.Json;

namespace Oblachka;

// Прогон системного режима целиком, без интерфейса: тот же путь, которым идёт окно.
//   Oblachka.Tests.exe --system-tunnel-check <ключ> <имя узла> <отчёт> <секунды> [--real-machine]
//
// Задействованы TunnelEndpoints, NetworkHelperLauncher, NetworkHelperHost,
// WindowsTunnelBackend, SingBoxTunnelProcess и SystemTunnelSession — то есть вся
// цепочка, а не её части. Нужны права администратора. По умолчанию — только в VM;
// --real-machine передаёт скрипт комплекта проверки (real-check) после подтверждения
// пользователя. Тогда собственные адреса машины и её резолверы в отчёт не попадают:
// отчёт пересылают, а это адрес человека и его провайдера.
internal static class SystemTunnelCheckCommand
{
    internal static int Run(string key, string nodeName, string reportPath, int holdSeconds, bool realMachine = false)
    {
        var log = new List<string>();
        string? own4 = null, own6 = null;
        void Add(string line) => log.Add(line);
        void Save() => File.WriteAllLines(reportPath, realMachine ? log.Select(l => Mask(l, own4, own6)) : log, new System.Text.UTF8Encoding(true));

        if (!NetworkHelperHost.IsElevated()) { Add("нужны права администратора"); Save(); return 2; }
        var virtualMachine = VmTunTickets.IsVirtualMachine();
        if (!virtualMachine && !realMachine) { Add("запуск разрешён только в виртуальной машине"); Save(); return 2; }
        Add("машина: " + (virtualMachine ? "виртуальная" : "реальная") + ", Windows " + Environment.OSVersion.Version);

        // Сеть гостя после смены DNS в начале прогона устаивается не сразу: первый запрос
        // бывает неудачным. Три попытки отличают это от настоящего отказа сервиса.
        ImportResult? imported = null;
        for (var attempt = 1; imported is null; attempt++)
        {
            try
            {
                using var fetch = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                imported = Subscriptions.FetchAsync(ServiceKey.ToSubscriptionUrl(key), fetch.Token).GetAwaiter().GetResult();
                if (attempt > 1) Add("подписка загружена с попытки " + attempt);
            }
            catch (Exception ex) when (attempt < 3) { Add("подписка, попытка " + attempt + ": " + ex.GetType().Name); Thread.Sleep(3000); }
            catch (Exception ex) { Add("подписка не загружена: " + ex.GetType().Name + (ex is UserError ? " — " + ex.Message : "")); Save(); return 2; }
        }

        var profile = imported.Profiles.FirstOrDefault(p => p.DisplayName.Contains(nodeName, StringComparison.OrdinalIgnoreCase));
        if (profile is null) { Add("узел не найден: " + nodeName); Save(); return 3; }
        Add("узел: " + profile.DisplayName + " · " + profile.Core + " · " + profile.Transport);

        // Исходный выход меряем до туннеля. На машине владельца постоянно включён свой VPN,
        // поэтому «известного» базового адреса не существует: его нужно измерять каждый раз.
        var baseline = Probe("https://api.ipify.org");
        if (!baseline.StartsWith("ошибка", StringComparison.Ordinal)) own4 = baseline;
        Add("исходный выход (до туннеля): " + baseline);
        var baseline6 = SessionWatch.Ipv6(url => Probe(url));
        if (!baseline6.StartsWith("ошибка", StringComparison.Ordinal)) own6 = baseline6;
        Add("исходный IPv6 (до туннеля): " + baseline6);
        Add("брандмауэр до туннеля: " + KillSwitchProbe.Describe());
        try { File.Delete(NetworkHelperHost.TracePath); } catch (IOException) { }
        Add("резолверы до туннеля: " + KillSwitchProbe.Resolvers());
        // Соединение открывается заранее: именно такие кадры уходили мимо туннеля в
        // захвате, и обрывает ли их блокировка — главный вопрос этого прогона.
        const string earlyHost = "example.com";
        var early = KillSwitchProbe.OpenBefore(earlyHost, 80, log);

        var engine = new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), "");
        HelperProcessLifetime? helper = null;
        SystemTunnelSession? session = null;
        try
        {
            using var token = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var plan = TunnelEndpoints.PrepareAsync(profile, token.Token).GetAwaiter().GetResult();
            Add("адрес входа закреплён: " + string.Join(',', plan.Peers));
            // Если вход узла совпал с текущим выходом машины, по IP ничего доказать нельзя:
            // адрес не изменится независимо от того, работает туннель или нет.
            if (plan.Peers.Contains(baseline, StringComparer.Ordinal))
                Add("ВНИМАНИЕ: вход узла совпадает с текущим выходом машины — перенаправление по IP этим прогоном не доказывается, выберите другой узел");

            // Узел без защиты и туннеля: так отказ узла или пути до него отличается от
            // отказа, который вносит сам системный режим.
            try
            {
                engine.StartAsync(plan.PinnedProfile, token.Token).GetAwaiter().GetResult();
                Add("до туннеля через SOCKS ядра: " + Socks(engine));
            }
            catch (UserError ex) { Add("до туннеля ядро не запустилось: " + ex.Message); }
            finally { engine.Stop(); }

            helper = HelperProcessLifetime.ConnectAsync(NetworkHelperLauncher.Launch, token.Token).GetAwaiter().GetResult();
            Add("помощник запущен и канал установлен");

            session = new SystemTunnelSession(engine, helper.Client);
            var states = new List<string>();
            session.Changed += s => states.Add(s.ToString());
            session.StartAsync(plan, token.Token).GetAwaiter().GetResult();
            Add("состояние сессии: " + session.State);
            Add("переходы: " + string.Join(" → ", states));
            Add("интерфейсы туннеля: " + string.Join(',', TunnelInterfaces.Ours()));
            Add("локальный порт ядра: " + engine.Port);

            Add("блокировка трафика мимо туннеля: " + (session.BlocksBypass ? "поставлена" : "НЕ поставлена"));
            Add("брандмауэр при поднятом туннеле: " + KillSwitchProbe.Describe());
            Add(KillSwitchProbe.ForeignAllows());
            Add(WfpStateProbe.Describe());
            Add(KillSwitchProbe.UseAfter(early, earlyHost));
            Add("резолверы при поднятом туннеле: " + KillSwitchProbe.Resolvers());
            Add(KillSwitchProbe.Resolve("api.ipify.org"));
            // Три пути по отдельности: без имени (только TCP через туннель), через локальный
            // SOCKS ядра (ядро → узел, туннель не участвует) и обычный запрос по имени.
            Add("без DNS, по адресу 1.1.1.1: " + Probe("https://1.1.1.1/cdn-cgi/trace", "ip="));
            Add("через SOCKS ядра: " + Socks(engine));

            var inside6 = SessionWatch.Ipv6(url => Probe(url));
            Add("IPv6 при поднятом туннеле: " + inside6 + (inside6 == baseline6 && !baseline6.StartsWith("ошибка", StringComparison.Ordinal)
                ? " — СОБСТВЕННЫЙ IPv6 МАШИНЫ, IPv6 идёт мимо туннеля" : ""));
            SessionWatch.Hold(holdSeconds, Add, url => Probe(url));
            var inside = Probe("https://api.ipify.org");
            var second = Probe("https://ifconfig.me/ip");
            Add("выходной IP: " + inside);
            Add("второй источник: " + second);
            // Вывод делается сравнением с измеренным исходным выходом, а не с зашитым адресом.
            // Ошибка — не «другой выход»: раньше отказ всех запросов читался как успех.
            Add(inside.StartsWith("ошибка", StringComparison.Ordinal)
                ? "ВЫВОД: через туннель связи нет — перенаправление НЕ подтверждено"
                : inside == baseline
                    ? "ВЫВОД: выход не изменился — перенаправление НЕ подтверждено"
                    : "ВЫВОД: выход изменился с " + baseline + " на " + inside + " — перенаправление подтверждено");
            if (inside != second) Add("ВНИМАНИЕ: источники разошлись, замеру верить нельзя");
            ForeignAllowProbe.Run(plan.Peers[0], Add);
            Add(ForeignAllowProbe.TunnelSourceToPeer(plan.Peers[0]));
            SystemTunnelCoreLoss.Run(session, engine, Add, () => Probe("https://api.ipify.org"));

            session.StopAsync().GetAwaiter().GetResult();
            Add("после остановки состояние: " + session.State);
            Add("остались интерфейсы: " + (TunnelInterfaces.Ours().Count == 0 ? "нет" : string.Join(',', TunnelInterfaces.Ours())));
            Add("брандмауэр после остановки: " + KillSwitchProbe.Describe());
            Add("после остановки " + WfpStateProbe.Describe());
            foreach (var step in KillSwitchProbe.HelperSteps()) Add(step);
            early?.Dispose();
            var after = Probe("https://api.ipify.org");
            Add("выходной IP после остановки: " + after);
            Add(after == baseline
                ? "ВЫВОД: выход вернулся к исходному — откат подтверждён"
                : "ВНИМАНИЕ: выход после остановки отличается от исходного");
            Add("итоговые переходы: " + string.Join(" → ", states));
            // Журнал занят помощником, пока тот жив: следующему он нужен свободным.
            helper.DisposeAsync().AsTask().GetAwaiter().GetResult(); helper = null;
            SystemTunnelHelperLoss.Run(plan, engine, Add, () => Probe("https://api.ipify.org"));
        }
        catch (Exception ex)
        {
            // Тип и место отказа секретов не содержат, а без них причину не найти.
            Add("ОТКАЗ: " + ex.GetType().Name + " — " + (ex is UserError ? ex.Message : "сообщение не выводится"));
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                Add("  внутренняя причина: " + inner.GetType().Name);
            var frames = (ex.StackTrace ?? "").Split('\n').Take(4).Select(f => f.Trim());
            foreach (var frame in frames) Add("  " + frame);
            Add("сессия создана: " + (session is not null));
            Add("помощник подключён: " + (helper is not null));
            Add("состояние сессии: " + (session?.State.ToString() ?? "нет"));
            // Помощник завершается в своём процессе, и со стороны это выглядит обрывом.
            // Его собственная причина лежит рядом с журналом.
            Add("причина у помощника: " + (File.Exists(NetworkHelperHost.FailurePath)
                ? File.ReadAllText(NetworkHelperHost.FailurePath) : "файла нет"));
            Add("брандмауэр после отказа: " + KillSwitchProbe.Describe());
            Add("остались интерфейсы: " + (TunnelInterfaces.Ours().Count == 0 ? "нет" : string.Join(',', TunnelInterfaces.Ours())));
            Save();
            try { session?.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { if (helper is not null) helper.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            engine.Dispose();
            return 2;
        }
        try { if (helper is not null) helper.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        engine.Dispose();
        Save();
        return 0;
    }

    // Запрос идёт мимо локального прокси: нас интересует, куда система выпускает трафик.
    private static string Probe(string url, string? line = null)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            var text = client.GetStringAsync(url).GetAwaiter().GetResult().Trim();
            return line is null ? text : text.Split('\n').FirstOrDefault(l => l.StartsWith(line, StringComparison.Ordinal)) ?? "ответ без " + line;
        }
        catch (Exception ex) { return "ошибка (" + ex.GetType().Name + ")"; }
    }

    // Собственные адреса машины заменяются метками: по ним видно, совпал ли выход с
    // исходным, но сам адрес не пересылается. Резолверы — адреса провайдера — скрыты.
    internal static string Mask(string line, string? own4, string? own6)
    {
        if (line.StartsWith("резолверы", StringComparison.Ordinal)) return line[..line.IndexOf(':')] + ": скрыто";
        if (own4 is not null) line = line.Replace(own4, "<свой IPv4>", StringComparison.Ordinal);
        if (own6 is not null) line = line.Replace(own6, "<свой IPv6>", StringComparison.Ordinal);
        return line;
    }

    private static string Socks(Engine engine)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = engine.ProbeAsync(new Uri("https://api.ipify.org"), timeout.Token).GetAwaiter().GetResult();
            return result.Success ? "работает, " + result.Milliseconds + " мс" : "НЕ работает (" + result.Failure + ")";
        }
        catch (Exception ex) { return "ошибка (" + ex.GetType().Name + ")"; }
    }
}
