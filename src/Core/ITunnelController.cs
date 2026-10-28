namespace Oblachka;

// UI and route search own operations, while implementations own core/network resources.
public interface ITunnelController : IDisposable
{
    int Port { get; }
    int? ProcessId { get; }
    ConnectionState State { get; }
    string CoreLabel { get; }
    event Action<ConnectionState, string>? Changed;
    Task StartAsync(Profile profile, CancellationToken token);
    Task<ProbeResult> ProbeAsync(Uri target, CancellationToken token);
    // Трафик несёт ядро туннеля (одно ядро, DirectOutbound): процесс ядра окна не нужен, проверка
    // связи идёт через переключатель ядра туннеля. null — обычное ядро окна.
    CoreStatsEntry? CarriedBy { get => null; set { } }
    // Физический интерфейс, к которому привязан работающий Xray (PhysicalBinding); null — следить не за чем.
    string? BoundInterface => null;
    void Stop();
}
