namespace RoutePilot;

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
    void Stop();
}
