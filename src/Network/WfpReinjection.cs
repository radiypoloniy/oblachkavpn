namespace Oblachka;

// zapret под защитой «Весь компьютер». WinDivert отправляет перехваченный пакет заново, и
// Windows видит его как новое соединение от System (pid 4, слой ALE_AUTH_CONNECT, reauth 0 —
// событие 5157 на стенде). Запрет мимо туннеля такое режет, и в 0.13.1 пропадал весь интернет.
//
// Пока winws работает под защитой, в её подслой добавляются разрешения только для System и
// только на порты, которые перехватывает стратегия (--wf-tcp, --wf-udp). Обычная программа так
// не пройдёт: её собственное соединение мимо туннеля режется раньше, ещё до перехвата. Порты
// берутся из стратегии, а не задаются здесь: обновлённый набор Flowseal с новым портом иначе
// снова оборвал бы соединение ядра с узлом.
//
// Номера фильтров — последние в диапазоне защиты: если служба упадёт, журнал снимет их вместе
// с остальными (WfpProtectionBackend.RemoveAll перебирает все номера).
public static class WfpReinjection
{
    public sealed record PortRange(int Protocol, int Low, int High);

    public const int Tcp = 6, Udp = 17;
    // Семь диапазонов на семейство адресов, два семейства.
    public const int PerFamily = 7;
    public const int Slots = PerFamily * 2;
    public static int First => WfpProtectionBackend.MaxFilters - Slots;

    private static readonly object Gate = new();
    private static bool active;

    // Порты перехвата из аргументов winws. Лишние диапазоны одного протокола сливаются по
    // ближайшему зазору: разрешение чуть шире, но порты перехвата не теряются.
    public static IReadOnlyList<PortRange> Ports(IEnumerable<string> args)
    {
        var ranges = new List<PortRange>();
        foreach (var arg in args)
        {
            var protocol = arg.StartsWith("--wf-tcp=", StringComparison.Ordinal) ? Tcp : arg.StartsWith("--wf-udp=", StringComparison.Ordinal) ? Udp : 0;
            if (protocol == 0) continue;
            foreach (var part in arg[(arg.IndexOf('=') + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var bounds = part.Split('-');
                if (bounds.Length is 1 or 2 && int.TryParse(bounds[0], out var low) && int.TryParse(bounds[^1], out var high) &&
                    low is >= 1 and <= 65535 && high >= low && high <= 65535)
                    ranges.Add(new(protocol, low, high));
            }
        }
        var merged = ranges.OrderBy(r => r.Protocol).ThenBy(r => r.Low).Aggregate(new List<PortRange>(), (list, r) =>
        {
            if (list.Count > 0 && list[^1].Protocol == r.Protocol && r.Low <= list[^1].High + 1)
                list[^1] = list[^1] with { High = Math.Max(list[^1].High, r.High) };
            else list.Add(r);
            return list;
        });
        while (merged.Count > PerFamily)
        {
            var best = -1;
            for (var i = 0; i + 1 < merged.Count; i++)
                if (merged[i].Protocol == merged[i + 1].Protocol &&
                    (best < 0 || merged[i + 1].Low - merged[i].High < merged[best + 1].Low - merged[best].High)) best = i;
            if (best < 0) break;
            merged[best] = merged[best] with { High = merged[best + 1].High };
            merged.RemoveAt(best + 1);
        }
        return merged.Take(PerFamily).ToArray();
    }

    public static IReadOnlyList<WfpFilter> Filters(IReadOnlyList<PortRange> ranges)
    {
        var list = new List<WfpFilter>();
        foreach (var v6 in new[] { false, true })
            for (var i = 0; i < Math.Min(ranges.Count, PerFamily); i++)
            {
                var r = ranges[i];
                var port = r.Low == r.High ? new WfpCondition(WfpConditionKind.RemotePort, Number: r.Low)
                    : new WfpCondition(WfpConditionKind.RemotePortRange, Number: r.Low, High: r.High);
                list.Add(new(WfpProtectionBackend.FilterKey(First + (v6 ? PerFamily : 0) + i),
                    ServiceBrand.Name + " — пакеты zapret" + (v6 ? " (IPv6)" : " (IPv4)"), v6, 13, true,
                    [new(WfpConditionKind.KernelProcess), new(WfpConditionKind.Protocol, Number: r.Protocol), port]));
            }
        return list;
    }

    // false — защиты нет (режим «Браузеры» или сессия снята): разрешать нечего.
    public static bool Allow(Func<IWfpEngine> open, IReadOnlyList<PortRange> ranges)
    {
        lock (Gate)
        {
            using var engine = open();
            if (!engine.SublayerExists(WfpProtectionBackend.SublayerKey)) { active = false; return false; }
            engine.Begin();
            try
            {
                for (var i = First; i < WfpProtectionBackend.MaxFilters; i++) engine.DeleteFilter(WfpProtectionBackend.FilterKey(i));
                foreach (var filter in Filters(ranges))
                    engine.AddFilter(filter, WfpProtectionBackend.ProviderKey, WfpProtectionBackend.SublayerKey);
                engine.Commit();
            }
            catch { engine.Abort(); throw; }
            active = true;
            return true;
        }
    }

    public static void Revoke(Func<IWfpEngine> open)
    {
        lock (Gate)
        {
            if (!active) return;
            using var engine = open();
            engine.Begin();
            try
            {
                for (var i = First; i < WfpProtectionBackend.MaxFilters; i++) engine.DeleteFilter(WfpProtectionBackend.FilterKey(i));
                engine.Commit();
            }
            catch { engine.Abort(); throw; }
            active = false;
        }
    }

    // Сессия снята целиком: фильтры ушли вместе с подслоем.
    public static void Forget() { lock (Gate) active = false; }
}
