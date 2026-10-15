using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Oblachka;

// Почему туннель не работает — по прямым проверкам из окна, мимо ядра:
//   Address   — до сервера не устанавливается TCP: адрес заблокирован или сервер выключен;
//   Handshake — TCP есть, но TLS-рукопожатие с тем же именем сайта рвётся: похоже на DPI;
//   Tunnel    — рукопожатие проходит, а туннель нет: протокол, ключи, сервер или фильтр по трафику;
//   Udp       — протокол поверх UDP, TCP-проверки о нём ничего не скажут;
//   Unknown   — проверить нельзя: под защитой «Весь компьютер» окну прямые соединения закрыты.
// Это подсказка для порядка перебора, а не приговор: фильтр может вести себя по-разному.
public enum Blockage { Unknown, Address, Handshake, Tunnel, Udp }

public sealed record Diagnosis(Blockage Kind, string Explanation);

public static class ConnectionDiagnosis
{
    public delegate Task<long?> TcpCheck(string host, int port, CancellationToken token);
    public delegate Task<bool> TlsCheck(string host, int port, string serverName, CancellationToken token);

    public static async Task<Diagnosis> RunAsync(Profile profile, CancellationToken token, TcpCheck? tcp = null, TlsCheck? tls = null)
    {
        if (ProfileServer.IsUdp(profile))
            return new(Blockage.Udp, "Сервер работает по UDP; если UDP в этой сети режут, помогут серверы на TCP.");
        var (host, port) = ProfileServer.Of(profile);
        var connected = await (tcp ?? ((h, p, t) => ServerPing.PingAsync(h, p, t)))(host, port, token).ConfigureAwait(false);
        if (connected == ServerPing.Blocked) return new(Blockage.Unknown, "Под защитой «Весь компьютер» прямую проверку сервера не сделать.");
        if (connected is null) return new(Blockage.Address, "До сервера не доходит соединение: адрес заблокирован в этой сети или сервер выключен.");
        if (ServerName(profile) is not { } name)
            return new(Blockage.Tunnel, "Сервер отвечает, но туннель не работает: дело в протоколе, ключах или самом сервере.");
        var handshake = await (tls ?? HandshakeAsync)(host, port, name, token).ConfigureAwait(false);
        return handshake
            ? new(Blockage.Tunnel, "Сервер отвечает и рукопожатие проходит, но туннель не работает: похоже, фильтр режет сам трафик или сервер не принимает ключ.")
            : new(Blockage.Handshake, "Соединение с сервером есть, но защищённое рукопожатие обрывается — похоже на фильтр DPI. Попробуем разрезать рукопожатие.");
    }

    // Имя сайта в рукопожатии: у REALITY — маскировочный сайт, у TLS — имя сервера.
    internal static string? ServerName(Profile profile)
    {
        JsonNode? node = profile.Core == CoreKind.Xray
            ? profile.Outbound["streamSettings"]?["realitySettings"]?["serverName"] ?? profile.Outbound["streamSettings"]?["tlsSettings"]?["serverName"]
            : profile.Outbound["tls"]?["enabled"]?.GetValue<bool>() == true ? profile.Outbound["tls"]?["server_name"] ?? profile.Outbound["server"] : null;
        return node?.GetValue<string>() is { Length: > 0 } name ? name : null;
    }

    // Пропускает ли сеть рукопожатие с этим именем. Сертификат не проверяется: важно,
    // дошёл ли ответ сервера, а не кому он принадлежит (у REALITY отвечает чужой сайт).
    private static async Task<bool> HandshakeAsync(string host, int port, string serverName, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            await using var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = serverName }, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException) { return false; }
    }
}
