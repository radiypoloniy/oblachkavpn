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

        var engine = new Engine(Path.Combine(AppContext.BaseDirectory, "tools", "xray.exe"), "");
        HelperProcessLifetime? helper = null;
        SystemTunnelSession? session = null;
        try
        {
            using var token = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var plan = TunnelEndpoints.PrepareAsync(profile, token.Token).GetAwaiter().GetResult();
            Add("адрес входа закреплён: " + string.Join(',', plan.Peers));

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
            Add("выходной IP: " + Probe("https://api.ipify.org"));
            Add("второй источник: " + Probe("https://ifconfig.me/ip"));

            session.StopAsync().GetAwaiter().GetResult();
            Add("после остановки состояние: " + session.State);
            Add("остались интерфейсы: " + (TunnelInterfaces.Ours().Count == 0 ? "нет" : string.Join(',', TunnelInterfaces.Ours())));
            Add("выходной IP после остановки: " + Probe("https://api.ipify.org"));
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
