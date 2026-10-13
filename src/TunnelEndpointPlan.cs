using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Oblachka;

// Source — профиль в том виде, в каком он в списке: по нему окно находит план узла.
public sealed record TunnelEndpointPlan(Profile PinnedProfile, string[] Peers)
{
    public Profile Source { get; init; } = PinnedProfile;
}

public static class TunnelEndpoints
{
    // Не больше, чем помощник принимает в одной аренде.
    public const int MaxPeers = 16;

    // Выбранный узел и запасные, на которые можно переключиться без снятия блокировки.
    // Первый обязан подготовиться; запасной, который не подготовился (имя не разрешилось,
    // формат не поддержан) или не помещается в лимит адресов, просто пропускается.
    public static async Task<IReadOnlyList<TunnelEndpointPlan>> PrepareSetAsync(IReadOnlyList<Profile> ordered,
        CancellationToken token, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        if (ordered.Count == 0) throw new UserError("Сначала выберите сервер.");
        var plans = new List<TunnelEndpointPlan> { await PrepareAsync(ordered[0], token, resolve).ConfigureAwait(false) };
        var peers = new HashSet<string>(plans[0].Peers, StringComparer.Ordinal);
        foreach (var profile in ordered.Skip(1))
        {
            token.ThrowIfCancellationRequested();
            TunnelEndpointPlan plan;
            try { plan = await PrepareAsync(profile, token, resolve).ConfigureAwait(false); }
            catch (Exception ex) when (ex is UserError or SocketException or ArgumentException) { continue; }
            if (peers.Union(plan.Peers).Count() > MaxPeers) continue;
            peers.UnionWith(plan.Peers);
            plans.Add(plan);
        }
        return plans;
    }

    public static async Task<TunnelEndpointPlan> PrepareAsync(Profile profile, CancellationToken token,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        var copy = (JsonObject)profile.Outbound.DeepClone();
        string host;
        if (profile.Core == CoreKind.Xray)
        {
            if (copy["streamSettings"]?["xhttpSettings"]?["extra"]?["downloadSettings"] is not null)
                throw new UserError("TUN: отдельный адрес XHTTP downstream ещё не поддержан.");
            host = Profiles.XrayEndpoint(copy)["address"]!.GetValue<string>();
        }
        else host = copy["server"]!.GetValue<string>();
        var addresses = IPAddress.TryParse(host, out var ip) ? new[] { ip } :
            await (resolve ?? Dns.GetHostAddressesAsync)(host, token).ConfigureAwait(false);
        var peers = addresses.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Distinct()
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).Select(a => a.ToString()).ToArray();
        NetworkHelperContracts.Validate(new(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, peers));
        if (profile.Core == CoreKind.Xray)
        {
            Profiles.XrayEndpoint(copy)["address"] = peers[0];
            var tls = copy["streamSettings"]?["tlsSettings"] as JsonObject;
            if (tls is not null && string.IsNullOrWhiteSpace(tls["serverName"]?.GetValue<string>())) tls["serverName"] = host;
        }
        else
        {
            copy["server"] = peers[0];
            var tls = copy["tls"]!.AsObject();
            if (string.IsNullOrWhiteSpace(tls["server_name"]?.GetValue<string>())) tls["server_name"] = host;
        }
        return new(profile with { Outbound = copy }, peers) { Source = profile };
    }
}
