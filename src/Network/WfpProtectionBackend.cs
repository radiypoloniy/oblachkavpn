using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Oblachka;

// Блокировка трафика мимо туннеля средствами Windows Filtering Platform.
//
// Блокировка брандмауэром Windows (KillSwitchBackend) переводила исходящее действие
// профилей в Block, но чужие разрешающие правила сильнее этого действия. На стенде
// curl с собственным разрешением соединился с адресом узла мимо туннеля, а таких правил
// у Windows и установленных программ больше сотни (аудит WFP на VM).
//
// Здесь — собственный подслой с наибольшим весом на слоях установления соединений IPv4
// и IPv6. Запрет в любом подслое сильнее разрешения в другом, если то не очистило право
// на действие, а правила брандмауэра его не очищают. В подслое разрешено только нужное:
// loopback, трафик в туннель, резолвер туннеля, ядра к адресам узлов и DHCP. Остальное
// запрещено. Брандмауэр Windows при этом не нужен вовсе: WFP работает и при выключенном.
//
// Ключи объектов постоянны и выводятся из номера фильтра, а не берутся из журнала:
// остатки после потерянного журнала снимаются так же, как штатная сессия.
public sealed class WfpProtectionBackend : INetworkSessionBackend
{
    public const string ResourceId = "wfp-protection";
    public static readonly Guid ProviderKey = new("6f1a3c52-0b7e-4d0e-9a1d-0b1a5c7e2f01");
    public static readonly Guid SublayerKey = new("6f1a3c52-0b7e-4d0e-9a1d-0b1a5c7e2f02");
    // Номеров с запасом: два ядра на 16 адресов узлов плюс общие фильтры обоих семейств.
    public const int MaxFilters = 64;
    private const ushort SublayerWeight = ushort.MaxValue;
    // Тип интерфейса wintun по IANA: IF_TYPE_PROP_VIRTUAL. Физические адаптеры — другие
    // типы (Ethernet 6, Wi-Fi 71), поэтому «не 53» и значит «мимо туннеля».
    public const int TunnelInterfaceType = 53;

    private readonly Func<IWfpEngine> open;
    private readonly string tools;

    public WfpProtectionBackend(Func<IWfpEngine> open, string? toolsDirectory = null)
    {
        this.open = open;
        tools = toolsDirectory ?? Path.Combine(AppContext.BaseDirectory, "tools");
    }

    public string XrayProgram => Path.Combine(tools, "xray.exe");
    public string SingBoxProgram => Path.Combine(tools, "sing-box.exe");

    public static Guid FilterKey(int index)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes("oblachka:wfp-filter:" + index));
        return new Guid(hash.AsSpan(0, 16));
    }

    public IReadOnlyList<NetworkChange> Prepare(ValidatedNetworkLease lease)
    {
        if (lease.Action != NetworkLeaseAction.Acquire) throw new UserError("Для запуска нужна команда Acquire.");
        // direct=1 — пользователь сам отправил часть трафика мимо туннеля правилами.
        var after = "sublayer=" + SublayerKey + (lease.HasDirect ? " direct=1" : "") +
            " peers=" + string.Join(',', lease.Peers.Select(p => p.ToString()));
        return new[] { new NetworkChange(NetworkResourceKind.WfpProtection, ResourceId, null, after) };
    }

    public NetworkWriteResult Apply(Guid leaseId, NetworkChange change)
    {
        var filters = Filters(Peers(change), change.After.Contains(" direct=1 ", StringComparison.Ordinal));
        using var engine = open();
        // Остатки прошлой сессии снимаются отдельной транзакцией: новая не должна
        // зависеть от того, что от старой осталось.
        if (engine.SublayerExists(SublayerKey)) RemoveAll(engine);
        engine.Begin();
        try
        {
            engine.AddProvider(ProviderKey, ServiceBrand.Name);
            engine.AddSublayer(SublayerKey, ProviderKey, ServiceBrand.Name + " — защита от утечек", SublayerWeight);
            foreach (var filter in filters) engine.AddFilter(filter, ProviderKey, SublayerKey);
            engine.Commit();
        }
        catch { engine.Abort(); throw; }
        if (!engine.SublayerExists(SublayerKey)) throw new IOException("Фильтрация Windows не сохранила подслой защиты.");
        return NetworkWriteResult.Changed;
    }

    public NetworkWriteResult Restore(Guid leaseId, NetworkChange change)
    {
        Peers(change);
        using var engine = open();
        if (!engine.SublayerExists(SublayerKey)) return NetworkWriteResult.Unchanged;
        RemoveAll(engine);
        if (engine.SublayerExists(SublayerKey)) throw new IOException("Фильтрация Windows не сняла подслой защиты.");
        return NetworkWriteResult.Changed;
    }

    private static void RemoveAll(IWfpEngine engine)
    {
        engine.Begin();
        try
        {
            for (var i = 0; i < MaxFilters; i++) engine.DeleteFilter(FilterKey(i));
            engine.DeleteSublayer(SublayerKey);
            engine.DeleteProvider(ProviderKey);
            engine.Commit();
        }
        catch { engine.Abort(); throw; }
    }

    // Состав фильтров. Вес внутри подслоя: разрешения выше запрета.
    public IReadOnlyList<WfpFilter> Filters(IReadOnlyList<IPAddress> peers, bool direct = false)
    {
        var list = new List<WfpFilter>();
        void Add(string name, bool v6, byte weight, bool permit, params WfpCondition[] conditions) =>
            list.Add(new(FilterKey(list.Count), ServiceBrand.Name + " — " + name, v6, weight, permit, conditions));
        void AddPacket(string name, bool v6, params WfpCondition[] conditions) =>
            list.Add(new(FilterKey(list.Count), ServiceBrand.Name + " — " + name, v6, 15, false, conditions, Packet: true));
        foreach (var v6 in new[] { false, true })
        {
            var family = v6 ? " (IPv6)" : " (IPv4)";
            var host = IPAddress.Parse(v6 ? SingBoxTunConfig.TunHost6 : SingBoxTunConfig.TunHost4);
            var resolver = IPAddress.Parse(v6 ? SingBoxTunConfig.TunResolver6 : SingBoxTunConfig.TunResolver4);
            Add("loopback" + family, v6, 15, true, new WfpCondition(WfpConditionKind.Loopback));
            Add("трафик в туннель" + family, v6, 14, true, new WfpCondition(WfpConditionKind.LocalAddress, host));
            Add("DNS туннеля" + family, v6, 14, true, new WfpCondition(WfpConditionKind.RemoteAddress, resolver));
            foreach (var peer in peers.Where(p => (p.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) == v6))
                foreach (var program in new[] { XrayProgram, SingBoxProgram })
                    Add("ядро к узлу" + family, v6, 13, true, new WfpCondition(WfpConditionKind.Application, Program: program),
                        new WfpCondition(WfpConditionKind.RemoteAddress, peer));
            // Правила «напрямую» исполняет ядро системного туннеля выходом direct. Без
            // таких правил ядрам открыты только адреса узлов.
            if (direct)
                Add("правила «напрямую»" + family, v6, 13, true, new WfpCondition(WfpConditionKind.Application, Program: SingBoxProgram));
            Add("DHCP" + family, v6, 12, true, new WfpCondition(WfpConditionKind.Protocol, Number: 17),
                new WfpCondition(WfpConditionKind.LocalPort, Number: v6 ? 546 : 68), new WfpCondition(WfpConditionKind.RemotePort, Number: v6 ? 547 : 67));
            Add("запрет мимо туннеля" + family, v6, 0, false);
            // Пакеты уже разрешённых потоков слой соединений не видит. На снятии туннеля
            // соединения, жившие в нём, закрываются, и их FIN уходил на физический адаптер
            // с адресом туннеля в отправителе. Адрес туннеля наружу выходить не должен.
            AddPacket("адрес туннеля только в туннель" + family, v6, new WfpCondition(WfpConditionKind.LocalAddress, host),
                new WfpCondition(WfpConditionKind.InterfaceTypeNot, Number: TunnelInterfaceType));
        }
        // Последние номера отданы разрешениям для пакетов zapret (WfpReinjection).
        if (list.Count > WfpReinjection.First) throw new UserError("Слишком много адресов узлов для защиты.");
        return list;
    }

    private static IReadOnlyList<IPAddress> Peers(NetworkChange change)
    {
        if (change is null || change.Kind != NetworkResourceKind.WfpProtection ||
            !string.Equals(change.ResourceId, ResourceId, StringComparison.Ordinal))
            throw new UserError("Этот backend обслуживает только защиту фильтрами Windows.");
        const string marker = "peers=";
        var at = change.After.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) throw new UserError("В изменении нет адреса входа узла.");
        // Адреса проверяются заново: строка из журнала могла быть изменена.
        var parsed = change.After[(at + marker.Length)..].Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => IPAddress.TryParse(p.Trim(), out var ip) ? ip : null).ToArray();
        if (parsed.Length is 0 or > 16 || parsed.Any(p => p is null)) throw new UserError("Адрес входа узла в изменении не разобран.");
        return parsed!;
    }
}
