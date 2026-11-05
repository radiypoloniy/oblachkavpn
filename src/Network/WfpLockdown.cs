using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Oblachka;

// «Без VPN — без интернета» (Lockdown): пока VPN не подключён, наружу ходят только сама Облачка
// и её ядра — обновить серверы, проверить их и подключиться. Остальным программам закрыто всё,
// кроме loopback, DHCP и (по выбору) локальной сети.
//
// Отдельный подслой ниже защиты сессии (WfpProtectionBackend): её разрешения сняли право на
// действие, поэтому при подключённом VPN трафик в туннель и ядра к узлам проходят, хоть здесь
// всё и запрещено. Запрет держится и между сессиями, и после выхода из окна.
//
// Объекты постоянные: Windows сама ставит их при загрузке, ещё до запуска службы, — иначе
// в первые секунды после включения компьютера программы шли бы мимо VPN. Снимает их только
// выключение настройки или удаление Облачки (--uninstall-service).
public static class WfpLockdown
{
    public static readonly Guid ProviderKey = new("6f1a3c52-0b7e-4d0e-9a1d-0b1a5c7e2f11");
    public static readonly Guid SublayerKey = new("6f1a3c52-0b7e-4d0e-9a1d-0b1a5c7e2f12");
    internal static readonly Guid StrictProviderKey = new("6f1a3c52-0b7e-4d0e-9a1d-0b1a5c7e2f21");
    internal static readonly Guid StrictSublayerKey = new("6f1a3c52-0b7e-4d0e-9a1d-0b1a5c7e2f22");
    public const ushort SublayerWeight = ushort.MaxValue - 2;
    internal const ushort StrictSublayerWeight = ushort.MaxValue - 1;
    public const int MaxFilters = 24;

    public static Guid FilterKey(int index) => FilterKey(index, strict: false);

    private static Guid FilterKey(int index, bool strict)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes((strict ? "oblachka:wfp-strict:" : "oblachka:wfp-lockdown:") + index));
        return new Guid(hash.AsSpan(0, 16));
    }

    // Облачка: окно и служба — один файл; ядра — из папки tools рядом.
    public static IReadOnlyList<string> Programs(string folder) =>
        [Path.Combine(folder, "Oblachka.exe"), Path.Combine(folder, "tools", "xray.exe"), Path.Combine(folder, "tools", "sing-box.exe"), TunCore.Program(Path.Combine(folder, "tools"))];

    // Окно разрешает имена серверов при восстановлении. Ядра получают разрешения после Acquire.
    internal static IReadOnlyList<string> StrictPrograms(string folder) => [Path.Combine(folder, "Oblachka.exe")];

    public static IReadOnlyList<WfpFilter> Filters(IReadOnlyList<string> programs, bool lan, bool strict = false)
    {
        var list = new List<WfpFilter>();
        void Add(string name, bool v6, byte weight, bool permit, params WfpCondition[] conditions) =>
            list.Add(new(FilterKey(list.Count, strict), ServiceBrand.Name + (strict ? " — строгая защита: " : " — без VPN: ") + name, v6, weight, permit, conditions));
        foreach (var v6 in new[] { false, true })
        {
            var family = v6 ? " (IPv6)" : " (IPv4)";
            Add("loopback" + family, v6, 15, true, new WfpCondition(WfpConditionKind.Loopback));
            Add("Облачка и её ядра" + family, v6, 14, true, programs.Select(p => new WfpCondition(WfpConditionKind.Application, Program: p)).ToArray());
            Add("DHCP" + family, v6, 13, true, new WfpCondition(WfpConditionKind.Protocol, Number: 17),
                new WfpCondition(WfpConditionKind.LocalPort, Number: v6 ? 546 : 68), new WfpCondition(WfpConditionKind.RemotePort, Number: v6 ? 547 : 67));
            if (lan)
            {
                // DNS роутера — это имена сайтов мимо VPN: локальной сети он не нужен.
                Add("DNS мимо VPN" + family, v6, 12, false, new WfpCondition(WfpConditionKind.RemotePort, Number: 53));
                Add("локальная сеть" + family, v6, 11, true, WfpProtectionBackend.LocalSubnets(v6).Concat(WfpProtectionBackend.Discovery(v6)).ToArray());
            }
            Add("запрет" + family, v6, 0, false);
            if (strict)
                list.Add(new(FilterKey(list.Count, strict), ServiceBrand.Name + " — адрес туннеля только в туннель" + family,
                    v6, 15, false,
                    [new(WfpConditionKind.LocalAddress, IPAddress.Parse(v6 ? SingBoxTunConfig.TunHost6 : SingBoxTunConfig.TunHost4)),
                     new(WfpConditionKind.InterfaceTypeNot, Number: WfpProtectionBackend.TunnelInterfaceType)], Packet: true));
        }
        return list;
    }

    public static bool IsOn(IWfpEngine engine) => engine.SublayerExists(SublayerKey);
    internal static bool StrictIntact(IWfpEngine engine, IReadOnlyList<string> programs) =>
        engine.SublayerExists(StrictSublayerKey) && Filters(programs, lan: false, strict: true).All(f => engine.FilterExists(f.Key));

    // Ставится заново целиком: прежний набор (другой выбор локальной сети) снимается в той же транзакции.
    public static void Apply(IWfpEngine engine, IReadOnlyList<string> programs, bool lan, bool strict = false)
    {
        var filters = Filters(programs, lan, strict);
        var provider = strict ? StrictProviderKey : ProviderKey;
        var sublayer = strict ? StrictSublayerKey : SublayerKey;
        var name = ServiceBrand.Name + (strict ? " — строгая защита" : " — без VPN без интернета");
        engine.Begin();
        try
        {
            if (engine.SublayerExists(sublayer)) Delete(engine, strict);
            engine.AddProvider(provider, name, persistent: true);
            engine.AddSublayer(sublayer, provider, name, strict ? StrictSublayerWeight : SublayerWeight, persistent: true);
            foreach (var filter in filters) engine.AddFilter(filter with { Persistent = true }, provider, sublayer);
            engine.Commit();
        }
        catch { engine.Abort(); throw; }
        if (!engine.SublayerExists(sublayer)) throw new IOException("Фильтрация Windows не сохранила постоянную блокировку.");
    }

    public static void Remove(IWfpEngine engine, bool strict = false)
    {
        var sublayer = strict ? StrictSublayerKey : SublayerKey;
        if (!engine.SublayerExists(sublayer)) return;
        engine.Begin();
        try { Delete(engine, strict); engine.Commit(); }
        catch { engine.Abort(); throw; }
        if (engine.SublayerExists(sublayer)) throw new IOException("Фильтрация Windows не сняла постоянную блокировку.");
    }

    private static void Delete(IWfpEngine engine, bool strict)
    {
        for (var i = 0; i < MaxFilters; i++) engine.DeleteFilter(FilterKey(i, strict));
        engine.DeleteSublayer(strict ? StrictSublayerKey : SublayerKey);
        engine.DeleteProvider(strict ? StrictProviderKey : ProviderKey);
    }
}

// Команда окна службе: включить или снять запрет без VPN.
public sealed record LockdownRequest(bool On, bool Lan);
