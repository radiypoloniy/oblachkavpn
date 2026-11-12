using System.Net;
using System.Net.Sockets;

namespace Oblachka;

public enum ProbeCore { Xray, SingBox, Oblachka }

public sealed record ProbeEndpoint(ProbeCore Core, string Address, int Port, bool Udp)
{
    internal IPAddress Validate()
    {
        if (!Enum.IsDefined(Core) || Port is < 1 or > 65535 || Port == 53 ||
            !IPAddress.TryParse(Address, out var ip) || ip.ToString() != Address ||
            ip.IsIPv4MappedToIPv6 || IPAddress.IsLoopback(ip) ||
            ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast)
            throw new UserError("Для замера нужен точный адрес сервера и его порт.");
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork &&
            (bytes[0] == 0 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 198 && bytes[1] is 18 or 19) ||
            ip.AddressFamily == AddressFamily.InterNetworkV6 &&
            (ip.ScopeId != 0 || bytes.AsSpan(0, 6).SequenceEqual(new byte[] { 0x20, 0x01, 0, 2, 0, 0 })))
            throw new UserError("Для замера нужен реальный адрес сервера.");
        return ip;
    }
}

public sealed record ProbeAccessRequest(Guid Id, ProbeEndpoint? Endpoint)
{
    internal void Validate()
    {
        if (Id == Guid.Empty) throw new UserError("Нет номера проверки сервера.");
        Endpoint?.Validate();
    }
}

internal sealed class WfpProbeAccess : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    internal static readonly object PolicyGate = new();
    private static readonly Dictionary<Guid, (IWfpEngine Engine, Timer Timer)> Active = new();

    internal static string CoreDirectory => Path.Combine(AppContext.BaseDirectory, "tools", "probe");

    internal static WfpFilter Filter(Guid id, ProbeEndpoint endpoint)
    {
        var ip = endpoint.Validate();
        var executable = endpoint.Core switch
        {
            ProbeCore.Xray => "xray.exe",
            ProbeCore.SingBox => "sing-box.exe",
            _ => TunCore.FileName
        };
        return new(id, ServiceBrand.Name + " — проверка сервера", ip.AddressFamily == AddressFamily.InterNetworkV6, 13, true,
            [new(WfpConditionKind.Application, Program: Path.Combine(CoreDirectory, executable)),
             new(WfpConditionKind.RemoteAddress, ip),
             new(WfpConditionKind.RemotePort, Number: endpoint.Port),
             new(WfpConditionKind.Protocol, Number: endpoint.Udp ? WfpReinjection.Udp : WfpReinjection.Tcp)]);
    }

    internal void Open(ProbeAccessRequest request)
    {
        var filter = Filter(request.Id, request.Endpoint!);
        lock (PolicyGate)
        {
            if (Active.Count >= ServerMeasurements.Parallel || Active.ContainsKey(request.Id))
                throw new UserError("Слишком много одновременных проверок.");
            var engine = WfpEngine.Open(dynamicSession: true);
            try
            {
                engine.AddFilter(filter, WfpProtectionBackend.ProviderKey, WfpProtectionBackend.SublayerKey);
                var expiry = new Timer(_ => Close(request.Id), null, Lifetime, Timeout.InfiniteTimeSpan);
                Active.Add(request.Id, (engine, expiry));
            }
            catch { engine.Dispose(); throw; }
        }
    }

    internal void Close(Guid id)
    {
        lock (PolicyGate)
        {
            if (!Active.Remove(id, out var entry)) return;
            entry.Timer.Dispose();
            entry.Engine.Dispose();
        }
    }

    public void Dispose() => CloseAll();

    internal static void CloseAll()
    {
        lock (PolicyGate)
        {
            foreach (var (engine, timer) in Active.Values) { timer.Dispose(); engine.Dispose(); }
            Active.Clear();
        }
    }
}
