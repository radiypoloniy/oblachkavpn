namespace Oblachka;

// Блокировка трафика мимо туннеля.
//
// Пока системный режим работает, исходящее действие всех профилей брандмауэра переведено
// в Block, а наружу разрешены ровно три вещи: трафик, уходящий в туннель (по локальному
// адресу самого туннеля), соединение программ ядер с адресом входа узла и DHCP.
// Отдельно запрещён DNS с любого адреса, кроме туннельного: у Windows есть собственные
// встроенные разрешения, и запрет по умолчанию их не отменяет — на стенде DNS сквозь них
// уходил на физический адаптер прямо при поднятом туннеле.
//
// Почему действие профиля, а не запрещающее правило: в брандмауэре Windows запрет всегда
// сильнее разрешения, поэтому исключения у запрещающего правила можно задать только внутри
// него самого — например, по интерфейсам. Интерфейс, появившийся позже (подключили Wi-Fi),
// в такое правило не попадёт и станет дырой. У действия профиля таких дыр нет.
//
// Что именно это закрывает, показал захват трафика снаружи гостя
// (проверка на VM): пока туннель работает, мимо него уходят только
// кадры соединений, открытых до его поднятия, а сразу после остановки наружу идёт всё.
// Поэтому kill switch стоит первым изменением в плане сессии: он встаёт до туннеля и
// снимается после него.
public sealed class KillSwitchBackend : INetworkSessionBackend
{
    public const string ResourceId = "kill-switch";
    public static readonly string TunnelRule = ServiceBrand.Name + " — трафик в туннель";
    // Адрес узла разрешён только программам ядер. Иначе к этому адресу мимо туннеля
    // ходила бы любая программа: разрешение действует на адрес, а не на того, кто идёт.
    public static readonly string PeerXrayRule = ServiceBrand.Name + " — соединение с сервером (Xray)";
    public static readonly string PeerSingBoxRule = ServiceBrand.Name + " — соединение с сервером (sing-box)";
    public static readonly string PeerTunCoreRule = ServiceBrand.Name + " — соединение с сервером (ядро туннеля)";
    // Прежнее разрешение без привязки к программе. Не ставится, но снимается: могло
    // остаться от сборки, которая его ставила.
    public static readonly string PeerRule = ServiceBrand.Name + " — соединение с сервером";
    public static readonly string DhcpRule = ServiceBrand.Name + " — получение адреса в сети";
    public static readonly string DnsRule = ServiceBrand.Name + " — запрет DNS мимо туннеля";
    public static readonly string DnsTcpRule = ServiceBrand.Name + " — запрет DNS по TCP мимо туннеля";
    public static readonly string TunnelDnsRule = ServiceBrand.Name + " — DNS через туннель";

    // Все адреса, кроме сети туннеля. Запрет нельзя сузить словом «кроме», зато можно
    // задать диапазонами: сеть туннеля занимает /30 и /126, и вокруг неё остаётся ровно
    // по две области.
    //
    // Нужен такой запрет затем, что у Windows есть собственные встроенные разрешения
    // («Основы сетей»), и запрет по умолчанию их не отменяет: DNS сквозь них уходил на
    // физический адаптер прямо при поднятом туннеле — 32 запроса за окно по захвату.
    //
    // Сравнение идёт по адресу получателя, а не отправителя. Сначала было наоборот —
    // «запретить DNS со всех адресов, кроме туннельного», — и на стенде это погасило
    // разрешение имён целиком: запрет накрыл и запросы к резолверу самого туннеля.
    // Адрес получателя известен и принадлежит нам, поэтому решение по нему надёжнее.
    private const string OutsideTunnelNetwork =
        // Записи выбраны не свободно, а по тому, что брандмауэр принимает и как понимает.
        // Проверено на стенде: «::1» он отвергает совсем, а «::» принимает, но понимает
        // как «любой адрес» — такое условие обнуляет исключение, и тогда запрет накрывает
        // и резолвер самого туннеля, то есть имена перестают разрешаться вовсе.
        // Поэтому IPv6 задан теми областями, где резолверы и живут, с вырезанной сетью
        // туннеля: глобальные адреса, link-local и локальные уникальные.
        "1.0.0.0-172.31.255.0,172.31.255.4-255.255.255.255," +
        "2000::-3fff:ffff:ffff:ffff:ffff:ffff:ffff:ffff," +
        "fe80::-febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff," +
        "fc00::-fdfe:dcba:9875:ffff:ffff:ffff:ffff:ffff," +
        "fdfe:dcba:9876::4-fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff";

    // Имена правил постоянны и не содержат идентификатора сессии: правило, оставшееся
    // после потери журнала, иначе стало бы невидимым для уборки. Одновременных сессий
    // не бывает — журнал этого не допускает.
    public static IReadOnlyList<string> RuleNames => new[] { TunnelRule, PeerXrayRule, PeerSingBoxRule, PeerTunCoreRule, DhcpRule, TunnelDnsRule, DnsRule, DnsTcpRule };
    private static IReadOnlyList<string> CleanupNames => RuleNames.Append(PeerRule).ToArray();

    private readonly IFirewallPolicy firewall;
    private readonly int[] profiles;
    private readonly string tools;

    // Ядра лежат в папке tools сборки — той же, из которой запущен помощник. Путь не
    // приходит по IPC: окно не вправе назначать, какой программе открыть выход.
    public KillSwitchBackend(IFirewallPolicy firewall, int[]? profiles = null, string? toolsDirectory = null)
    {
        this.firewall = firewall;
        this.profiles = profiles ?? WindowsFirewallPolicy.AllProfiles;
        tools = toolsDirectory ?? Path.Combine(AppContext.BaseDirectory, "tools");
    }

    public string XrayProgram => Path.Combine(tools, "xray.exe");
    public string SingBoxProgram => Path.Combine(tools, "sing-box.exe");
    public string TunCoreProgram => TunCore.Program(tools);

    // Блокировка имеет смысл только при включённом брандмауэре: иначе исходящее действие
    // профиля ни на что не влияет, и мы бы обещали защиту, которой нет. Отказ брандмауэра
    // на этом запросе означает то же самое — защиту поставить нечем.
    public bool IsAvailable
    {
        get
        {
            try { return profiles.All(firewall.IsEnabled); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UserError) { return false; }
        }
    }

    public IReadOnlyList<NetworkChange> Prepare(ValidatedNetworkLease lease)
    {
        if (lease.Action != NetworkLeaseAction.Acquire) throw new UserError("Для запуска нужна команда Acquire.");
        // Выключенный брандмауэр молча превратил бы блокировку в обещание: действие
        // профиля в этом случае ни на что не влияет. Лучше отказать явно.
        var off = profiles.Where(p => !firewall.IsEnabled(p)).ToArray();
        if (off.Length > 0)
            throw new UserError("Брандмауэр Windows выключен для профиля " + Names(off) +
                ". Пока он выключен, блокировать трафик мимо VPN нечем: включите брандмауэр " +
                "или снимите галочку блокировки.");
        var after = "outbound=Block peers=" + string.Join(',', lease.Peers.Select(p => p.ToString()));
        return new[] { new NetworkChange(NetworkResourceKind.KillSwitch, ResourceId, Current(), after) };
    }

    public NetworkWriteResult Apply(Guid leaseId, NetworkChange change)
    {
        var peers = Owned(change);
        // Чужую настройку не перебиваем: если исходящее действие уже не то, что мы
        // записали в план, значит распоряжается кто-то другой.
        if (Current() != change.Before) return NetworkWriteResult.Conflict;

        // Остатки с нашими же именами могли пережить потерю журнала. Правило создаётся
        // заново, поэтому старое снимается до, а не после.
        foreach (var name in CleanupNames) firewall.RemoveRule(name);
        firewall.AddRule(new FirewallRule(TunnelRule,
            LocalAddresses: SingBoxTunConfig.TunHost4 + "," + SingBoxTunConfig.TunHost6));
        firewall.AddRule(new FirewallRule(PeerXrayRule, RemoteAddresses: peers, Program: XrayProgram));
        firewall.AddRule(new FirewallRule(PeerSingBoxRule, RemoteAddresses: peers, Program: SingBoxProgram));
        firewall.AddRule(new FirewallRule(PeerTunCoreRule, RemoteAddresses: peers, Program: TunCoreProgram));
        firewall.AddRule(new FirewallRule(DhcpRule, WindowsFirewallPolicy.Udp,
            LocalPorts: "68", RemotePorts: "67"));
        // Резолверу туннеля нужно собственное разрешение. Правило по локальному адресу
        // его не покрывает: на стенде при запрете по умолчанию имена не разрешались,
        // хотя сам туннель трафик нёс. Решение принимается по адресу получателя, и этот
        // адрес принадлежит нам — его ядро само прописывает интерфейсу туннеля.
        firewall.AddRule(new FirewallRule(TunnelDnsRule,
            RemoteAddresses: SingBoxTunConfig.TunResolver4 + "," + SingBoxTunConfig.TunResolver6));
        // Запрет сильнее разрешения, поэтому встроенные правила Windows он перебивает.
        // Выпускается только DNS к резолверу самого туннеля — он и разрешает имена.
        firewall.AddRule(new FirewallRule(DnsRule, WindowsFirewallPolicy.Udp,
            RemoteAddresses: OutsideTunnelNetwork, RemotePorts: "53", Action: FirewallAction.Block));
        firewall.AddRule(new FirewallRule(DnsTcpRule, WindowsFirewallPolicy.Tcp,
            RemoteAddresses: OutsideTunnelNetwork, RemotePorts: "53", Action: FirewallAction.Block));

        // Разрешения ставятся до запрета: наоборот означало бы окно, в котором наружу
        // не проходит и сам туннель.
        foreach (var profile in profiles) firewall.SetOutboundAction(profile, FirewallAction.Block);
        foreach (var profile in profiles)
            if (firewall.OutboundAction(profile) != FirewallAction.Block)
                throw new IOException("Брандмауэр не принял запрет исходящего трафика для профиля " + profile + ".");
        foreach (var name in RuleNames)
            if (!firewall.RuleExists(name))
                throw new IOException("Брандмауэр не сохранил правило «" + name + "».");
        return NetworkWriteResult.Changed;
    }

    public NetworkWriteResult Restore(Guid leaseId, NetworkChange change)
    {
        Owned(change);
        var current = Current();
        if (current == change.Before)
        {
            // Запрет не стоял: либо он не успел примениться, либо это восстановление по
            // чужому незавершённому журналу. Остатки правил всё равно убираем.
            RemoveRules();
            return NetworkWriteResult.Unchanged;
        }
        // Сравнивать нужно с состоянием, посчитанным тем же способом, что и Before.
        // Строка After описывает изменение для человека и несёт адрес узла — в этом
        // формате она с состоянием брандмауэра не совпадёт никогда.
        if (current != Blocked()) return NetworkWriteResult.Conflict;

        // Сначала возвращается исходящее действие, потом снимаются разрешения. Обратный
        // порядок оставил бы компьютер без сети, если бы снятие сорвалось посередине:
        // пользователь к этому моменту уже попросил выключить режим.
        foreach (var (profile, action) in Parse(change.Before)) firewall.SetOutboundAction(profile, action);
        if (Current() != change.Before)
            throw new IOException("Брандмауэр не вернул исходящее действие профилей.");
        RemoveRules();
        return NetworkWriteResult.Changed;
    }

    private void RemoveRules()
    {
        foreach (var name in CleanupNames) firewall.RemoveRule(name);
        // Снятие проверяется, а не предполагается: оставшееся разрешение — это дыра,
        // а оставшийся запрет — молча сломанный DNS. И то и другое переживёт сессию.
        var left = CleanupNames.Where(firewall.RuleExists).ToArray();
        if (left.Length > 0)
            throw new IOException("Брандмауэр не снял правила: " + string.Join(", ", left) + ".");
    }

    private string Owned(NetworkChange change)
    {
        if (change is null || change.Kind != NetworkResourceKind.KillSwitch ||
            !string.Equals(change.ResourceId, ResourceId, StringComparison.Ordinal))
            throw new UserError("Этот backend обслуживает только блокировку трафика мимо туннеля.");
        const string marker = "peers=";
        var at = change.After.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) throw new UserError("В изменении нет адреса входа узла.");
        var peers = change.After[(at + marker.Length)..].Trim();
        // Адреса проверяются заново: разбирается строка из журнала, который мог быть изменён.
        var parsed = peers.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parsed.Length == 0 || parsed.Any(p => !System.Net.IPAddress.TryParse(p, out _)))
            throw new UserError("Адрес входа узла в изменении не разобран.");
        return string.Join(',', parsed);
    }

    private string Current() => Describe(profiles.Select(p => (p, firewall.OutboundAction(p))));

    private string Blocked() => Describe(profiles.Select(p => (p, FirewallAction.Block)));

    private static string Describe(IEnumerable<(int Profile, FirewallAction Action)> state) =>
        "outbound=" + string.Join(' ', state.Select(s => s.Profile + ":" + s.Action));

    // Разбор того, что записал Describe этой же сборки. Непонятная строка — отказ, а не
    // догадка: восстанавливать действие профиля наугад нельзя.
    private static IReadOnlyList<(int Profile, FirewallAction Action)> Parse(string? before)
    {
        const string marker = "outbound=";
        if (before is null || !before.StartsWith(marker, StringComparison.Ordinal))
            throw new UserError("В журнале нет прежнего исходящего действия брандмауэра.");
        var result = new List<(int, FirewallAction)>();
        foreach (var part in before[marker.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var halves = part.Split(':');
            if (halves.Length != 2 || !int.TryParse(halves[0], out var profile) ||
                !Enum.TryParse<FirewallAction>(halves[1], out var action))
                throw new UserError("Прежнее исходящее действие брандмауэра не разобрано.");
            result.Add((profile, action));
        }
        if (result.Count == 0) throw new UserError("Прежнее исходящее действие брандмауэра не разобрано.");
        return result;
    }

    private static string Names(int[] profiles) => string.Join(", ", profiles.Select(p => p switch
    {
        1 => "домена",
        2 => "частной сети",
        4 => "общественной сети",
        _ => "номер " + p
    }));
}
