namespace Oblachka;

// Процесс ядра, который держит системный туннель. Остановка — через Dispose.
public interface ITunnelProcess : IDisposable
{
    bool IsRunning { get; }
}

// Реальный сетевой backend Windows.
//
// Выбор пути данных сделан по итогам проверки на VM:
// маршруты, DNS и исключение адреса входа ставит и снимает само ядро sing-box через
// auto_route и route_exclude_address. Поэтому у сессии ровно одно изменение — процесс
// ядра вместе с его интерфейсом, а не список маршрутов. Проверка показала, что при
// завершении процесса Windows убирает адаптер wintun и все его маршруты сама. Штатно
// ядро останавливается мягко и удаляет адаптер само; за убитым Windows убирает с
// задержкой до минут.
//
// Отсюда же два правила, записанные в коде ниже:
//   1. Процесс обязан быть привязан к Job Object. На стенде отдельно запущенное ядро
//      пережило завершение запускавшей задачи и оставило интерфейс с 32 маршрутами.
//   2. Отсутствие интерфейса при восстановлении — это успех, а не ошибка: ядро могло
//      быть уже убито вместе с владельцем.
public sealed class WindowsTunnelBackend : INetworkSessionBackend, IDisposable
{
    public const string InterfacePrefix = "Obl-";
    // Адаптер wintun появляется и исчезает не мгновенно: снятие происходит уже после
    // смерти процесса ядра. Срок должен быть заметно меньше того, что готов ждать клиент
    // канала, иначе он разорвёт связь раньше, чем помощник закончит.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(25);

    private readonly Func<ValidatedNetworkLease, CancellationToken, ITunnelProcess> startCore;
    private readonly Func<string, bool> interfaceExists;
    // Снятие ждёт, пока адаптер перестанет работать, а не пока исчезнет из списка: .NET 10
    // перечисляет и записи об уже удалённых адаптерах (в состоянии Down). Из-за такой записи
    // остановка после смены сервера ждала полный срок, 25 с, хотя ядро закрылось за секунду.
    private readonly Func<string, bool> interfaceUp;
    private readonly TimeSpan timeout;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private ITunnelProcess? core;
    private Guid owner;
    // Правила сессии в журнал не помещаются (до 200 записей), а нужны только для запуска
    // ядра сразу после подготовки. Журнал хранит их число, восстановлению они не нужны.
    private ValidatedNetworkLease? prepared;

    public WindowsTunnelBackend(Func<ValidatedNetworkLease, CancellationToken, ITunnelProcess> startCore,
        Func<string, bool> interfaceExists, TimeSpan? timeout = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<string, bool>? interfaceUp = null)
    {
        this.startCore = startCore;
        this.interfaceExists = interfaceExists;
        this.interfaceUp = interfaceUp ?? interfaceExists;
        this.timeout = timeout ?? DefaultTimeout;
        this.delay = delay ?? Task.Delay;
    }

    // Имя интерфейса выводится из идентификатора сессии и совпадает с тем, что кладёт
    // в конфигурацию SingBoxTunConfig. Из IPC имя не принимается.
    public static string InterfaceName(Guid leaseId) => InterfacePrefix + leaseId.ToString("N")[..8];

    public IReadOnlyList<NetworkChange> Prepare(ValidatedNetworkLease lease)
    {
        if (lease.Action != NetworkLeaseAction.Acquire) throw new UserError("Для запуска нужна команда Acquire.");
        prepared = lease;
        var description = "tun=" + InterfaceName(lease.LeaseId) +
            " socks=127.0.0.1:" + lease.LoopbackPort +
            " rules=" + (lease.SiteRules.Count + lease.ProgramRules.Count) +
            " exclude=" + string.Join(',', lease.Peers);
        return new[] { new NetworkChange(NetworkResourceKind.TunnelInterface, InterfaceName(lease.LeaseId), null, description) };
    }

    public NetworkWriteResult Apply(Guid leaseId, NetworkChange change)
    {
        var name = Owned(leaseId, change);
        if (core is { IsRunning: true }) throw new UserError("Системный туннель уже запущен.");
        // Чужой интерфейс с нашим именем не трогаем: его владельца мы не знаем.
        if (interfaceExists(name)) return NetworkWriteResult.Conflict;

        var lease = Describe(leaseId, change);
        // Правила и вход прямого трафика ядра — из проверенной аренды этой сессии: в журнал они не
        // пишутся (в описании входа — пароль). Потерянный здесь вход (0.10) оставлял «Российские
        // сайты напрямую» без выхода: ядро слало их на порт, где никого не было.
        if (prepared is { } rules && rules.LeaseId == leaseId)
            lease = lease with { Sites = rules.SiteRules, Programs = rules.ProgramRules, CoreDirect = rules.CoreDirect };
        var started = startCore(lease, CancellationToken.None);
        try
        {
            if (!Wait(() => interfaceExists(name)))
                throw new UserError("Системный туннель не поднялся: интерфейс не появился.");
            if (!started.IsRunning)
                throw new UserError("Ядро системного туннеля завершилось при запуске.");
        }
        catch
        {
            started.Dispose();
            throw;
        }
        core = started;
        owner = leaseId;
        return NetworkWriteResult.Changed;
    }

    public NetworkWriteResult Restore(Guid leaseId, NetworkChange change)
    {
        var name = Owned(leaseId, change);
        // Ссылку держим локально: поле обнуляется сразу, а судить о том, умерло ли ядро,
        // нужно именно по нему.
        ITunnelProcess? halted = null;
        if (core is not null && owner == leaseId)
        {
            halted = core;
            core.Dispose();
            core = null;
            owner = Guid.Empty;
        }
        // Нечего было останавливать: это восстановление по чужому незавершённому журналу,
        // например после перезагрузки. Ждать исчезновения адаптера бессмысленно — своего
        // ядра у нас нет, а задержавшийся остаток всё равно ничего не несёт. Ожидание здесь
        // стоило помощнику 25 секунд ДО открытия канала, и клиент не дожидался подключения.
        if (halted is null) return NetworkWriteResult.Unchanged;

        // Ядро только что остановлено: ждём, пока Windows уберёт адаптер.
        if (Wait(() => !interfaceUp(name))) return NetworkWriteResult.Changed;

        // Адаптер задержался. На стенде это наблюдалось многократно: Windows убирает
        // wintun уже после смерти процесса, и задержка доходит до минут. Трафик при этом
        // возвращается на обычный выход сразу, как только ядро умерло, — проверено по
        // выходному IP. Поэтому решающим признаком служит процесс, а не адаптер:
        // если ядра нет, туннель не может ничего нести, и сессия считается снятой.
        // Иначе мы бы навсегда блокировали следующий запуск из-за безобидного остатка.
        if (!halted.IsRunning) return NetworkWriteResult.Changed;
        throw new IOException("Ядро системного туннеля не остановилось: " + name);
    }

    // Владение проверяется по имени интерфейса, выведенному из идентификатора сессии:
    // запись из чужой сессии не может распоряжаться нашим интерфейсом.
    private static string Owned(Guid leaseId, NetworkChange change)
    {
        if (change is null || change.Kind != NetworkResourceKind.TunnelInterface)
            throw new UserError("Этот backend обслуживает только интерфейс системного туннеля.");
        var name = InterfaceName(leaseId);
        if (!string.Equals(change.ResourceId, name, StringComparison.Ordinal))
            throw new UserError("Изменение принадлежит другой сетевой сессии.");
        return name;
    }

    // Параметры запуска восстанавливаются из описания, которое составил Prepare этой же
    // сборки. Произвольные строки из IPC сюда не попадают: Validate уже отработал.
    private static ValidatedNetworkLease Describe(Guid leaseId, NetworkChange change)
    {
        var parts = change.After.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var port = 0;
        var peers = Array.Empty<string>();
        foreach (var part in parts)
        {
            if (part.StartsWith("socks=127.0.0.1:", StringComparison.Ordinal))
                int.TryParse(part["socks=127.0.0.1:".Length..], out port);
            else if (part.StartsWith("exclude=", StringComparison.Ordinal))
                peers = part["exclude=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries);
        }
        return NetworkHelperContracts.Validate(new(1, leaseId, NetworkLeaseAction.Acquire, port, peers));
    }

    private bool Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            delay(TimeSpan.FromMilliseconds(250), CancellationToken.None).GetAwaiter().GetResult();
        }
        return condition();
    }

    public void Dispose()
    {
        core?.Dispose();
        core = null;
    }
}
