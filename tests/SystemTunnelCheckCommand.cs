using System.Text.Json;

namespace Oblachka;

// Прогон системного режима целиком, без интерфейса: тот же путь, которым идёт окно.
//   Oblachka.Tests.exe --system-tunnel-check <ключ> <имя узла> <отчёт> <секунды>
//
// Задействованы TunnelEndpoints, NetworkHelperLauncher, NetworkHelperHost,
// WindowsTunnelBackend, SingBoxTunnelProcess и SystemTunnelSession — то есть вся
// цепочка, а не её части. Нужны права администратора; запуск только в одноразовой VM.
internal static class SystemTunnelCheckCommand
{
    internal static int Run(string key, string nodeName, string reportPath, int holdSeconds)
    {
        var log = new List<string>();
        void Add(string line) => log.Add(line);
        void Save() => File.WriteAllLines(reportPath, log, new System.Text.UTF8Encoding(true));

        if (!NetworkHelperHost.IsElevated()) { Add("нужны права администратора"); Save(); return 2; }
        if (!VmTunTickets.IsVirtualMachine()) { Add("запуск разрешён только в виртуальной машине"); Save(); return 2; }

        ImportResult imported;
        try
        {
            using var fetch = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            imported = Subscriptions.FetchAsync(ServiceKey.ToSubscriptionUrl(key), fetch.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) { Add("подписка не загружена: " + ex.GetType().Name); Save(); return 2; }

        var profile = imported.Profiles.FirstOrDefault(p => p.DisplayName.Contains(nodeName, StringComparison.OrdinalIgnoreCase));
        if (profile is null) { Add("узел не найден: " + nodeName); Save(); return 3; }
        Add("узел: " + profile.DisplayName + " · " + profile.Core + " · " + profile.Transport);

        // Исходный выход меряем до туннеля. На машине владельца постоянно включён свой VPN,
        // поэтому «известного» базового адреса не существует: его нужно измерять каждый раз.
        var baseline = Probe("https://api.ipify.org");
        Add("исходный выход (до туннеля): " + baseline);

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

            Thread.Sleep(TimeSpan.FromSeconds(Math.Clamp(holdSeconds, 1, 120)));
            var inside = Probe("https://api.ipify.org");
            var second = Probe("https://ifconfig.me/ip");
            Add("выходной IP: " + inside);
            Add("второй источник: " + second);
            // Вывод делается сравнением с измеренным исходным выходом, а не с зашитым адресом.
            Add(inside == baseline
                ? "ВЫВОД: выход не изменился — перенаправление НЕ подтверждено"
                : "ВЫВОД: выход изменился с " + baseline + " на " + inside + " — перенаправление подтверждено");
            if (inside != second) Add("ВНИМАНИЕ: источники разошлись, замеру верить нельзя");

            session.StopAsync().GetAwaiter().GetResult();
            Add("после остановки состояние: " + session.State);
            Add("остались интерфейсы: " + (TunnelInterfaces.Ours().Count == 0 ? "нет" : string.Join(',', TunnelInterfaces.Ours())));
            var after = Probe("https://api.ipify.org");
            Add("выходной IP после остановки: " + after);
            Add(after == baseline
                ? "ВЫВОД: выход вернулся к исходному — откат подтверждён"
                : "ВНИМАНИЕ: выход после остановки отличается от исходного");
            Add("итоговые переходы: " + string.Join(" → ", states));
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
    private static string Probe(string url)
    {
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
            return client.GetStringAsync(url).GetAwaiter().GetResult().Trim();
        }
        catch (Exception ex) { return "ошибка (" + ex.GetType().Name + ")"; }
    }
}
