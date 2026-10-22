namespace Oblachka;

// Сетевая часть системного режима целиком: блокировка трафика мимо туннеля и сам туннель.
//
// Порядок задан здесь и только здесь. Сессия применяет изменения по списку и снимает их в
// обратном порядке, поэтому блокировка, стоящая первой, встаёт до поднятия туннеля и
// снимается после его остановки — ровно там, где захват трафика нашёл утечки.
//
// Блокировка не вынесена в отдельную настройку: системный режим и означает «весь трафик
// через туннель». Основная блокировка — фильтры WFP: чужие разрешения брандмауэра её не
// пробивают, и брандмауэр для неё не нужен. Если служба фильтрации недоступна, остаётся
// прежняя блокировка брандмауэром, а если и он выключен — режим работает без блокировки,
// и об этом говорит сам план: в нём просто нет изменения защиты. Клиенту возвращается
// запись сессии, и он сообщает пользователю то, что в ней есть, а не то, на что надеялись.
public sealed class SystemNetworkBackend : INetworkSessionBackend, IDisposable
{
    private readonly WindowsTunnelBackend tunnel;
    private readonly KillSwitchBackend? killSwitch;
    private readonly WfpProtectionBackend? wfp;

    public SystemNetworkBackend(WindowsTunnelBackend tunnel, KillSwitchBackend? killSwitch, WfpProtectionBackend? wfp = null)
    {
        this.tunnel = tunnel;
        this.killSwitch = killSwitch;
        this.wfp = wfp;
    }

    public static bool Blocks(NetworkSessionRecord? record) =>
        record is not null && record.Changes.Any(c => c.Kind is NetworkResourceKind.WfpProtection or NetworkResourceKind.KillSwitch);

    public IReadOnlyList<NetworkChange> Prepare(ValidatedNetworkLease lease)
    {
        var changes = new List<NetworkChange>();
        if (wfp is not null) changes.AddRange(wfp.Prepare(lease));
        else if (killSwitch is { IsAvailable: true }) changes.AddRange(killSwitch.Prepare(lease));
        changes.AddRange(tunnel.Prepare(lease));
        return changes;
    }

    public NetworkWriteResult Apply(Guid leaseId, NetworkChange change) => Pick(change).Apply(leaseId, change);

    public NetworkWriteResult Restore(Guid leaseId, NetworkChange change) => Pick(change).Restore(leaseId, change);

    public bool Intact(NetworkChange change) => change.Kind != NetworkResourceKind.WfpProtection || wfp is null || wfp.Intact(change);

    // Восстановление должно работать и тогда, когда блокировку сейчас поставить нельзя:
    // запись в журнале могла остаться от сессии, когда брандмауэр был включён. Поэтому
    // здесь проверяется наличие backend, а не его готовность ставить новую блокировку.
    private INetworkSessionBackend Pick(NetworkChange change) => change?.Kind switch
    {
        NetworkResourceKind.WfpProtection => wfp
            ?? throw new UserError("Защиту фильтрами снять нечем: служба фильтрации Windows недоступна."),
        NetworkResourceKind.KillSwitch => killSwitch
            ?? throw new UserError("Блокировку трафика снять нечем: брандмауэр Windows недоступен."),
        NetworkResourceKind.TunnelInterface => tunnel,
        _ => throw new UserError("Это изменение не относится к системному режиму.")
    };

    public void Dispose() => tunnel.Dispose();
}
