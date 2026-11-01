namespace Oblachka;

public sealed class ClientPathHealth
{
    public TunnelHealth Tunnel { get; } = new();
    public TunnelHealth Direct { get; } = new();
    public bool Rebuilt { get; set; }
    public bool Broken { get; set; }
    public string Problem { get; set; } = "";
    public bool DirectOnly { get; private set; }
    public bool ShouldRepair => Tunnel.FailedRounds >= 2 || Direct.FailedRounds >= 2;
    private readonly Func<Uri, CancellationToken, Task<ProbeResult>> probe;
    public ClientPathHealth(Func<Uri, CancellationToken, Task<ProbeResult>>? probe = null) { this.probe = probe ?? SystemPathProbe.RunAsync; }

    public async Task<string?> CheckAsync(bool active, bool direct, CancellationToken token)
    {
        if (!active) { Tunnel.Reset(); Direct.Reset(); return null; }
        var tunnel = await Tunnel.CheckAsync(probe, TunnelHealth.Targets, token);
        DirectOnly = false;
        if (!tunnel.Reachable) return "Сервер отвечает, но программы компьютера через туннель в интернет не выходят.";
        if (direct)
        {
            var result = await Direct.CheckAsync(probe, SystemPathProbe.DirectTargets, token);
            if (!result.Reachable) { DirectOnly = true; return "Сайты через VPN открываются, а российские, которые идут напрямую, — нет."; }
        }
        else Direct.Reset();
        return null;
    }
    public void Reset() { Rebuilt = Broken = DirectOnly = false; Tunnel.Reset(); Direct.Reset(); }
}
