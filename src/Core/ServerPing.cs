using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Oblachka;

// Быстрый отклик серверов: время TCP-рукопожатия с адресом узла, как «пинг» в Happ и Throne.
//
// Ядро не запускается, поэтому замер всех узлов параллельно занимает секунды, и при запуске
// окна список сразу показывает, из чего выбирать. Работоспособность протокола он не
// доказывает — это делает полный замер «Обновить пинг» (ServerMeasurements) через ядро.
// Hysteria2 работает по UDP: TCP-рукопожатия у него нет, такие узлы пропускаются.
public static class ServerPing
{
    public const int Parallel = 8;
    public const long Blocked = -1;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static (string Host, int Port)? Endpoint(Profile profile)
    {
        try { return ProfileServer.IsUdp(profile) ? null : ProfileServer.Of(profile); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NullReferenceException or ArgumentException) { return null; }
    }

    // Отклик каждого узла приходит в measured по мере готовности: null — узел не ответил.
    // Соединение, запрещённое на этом компьютере (защита «Весь компьютер» пропускает к узлам
    // только ядро), про узел ничего не говорит: такой результат не сообщается вовсе.
    public static async Task PingAllAsync(IReadOnlyList<Profile> profiles, Action<Profile, long?> measured, CancellationToken token,
        Func<string, int, CancellationToken, Task<long?>>? ping = null)
    {
        using var gate = new SemaphoreSlim(Parallel);
        var tasks = profiles.Select(async profile =>
        {
            if (Endpoint(profile) is not { } endpoint) return;
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var result = await (ping ?? PingAsync)(endpoint.Host, endpoint.Port, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (result is not Blocked) measured(profile, result);
            }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // Имя разрешается до замера: в отклик идёт только рукопожатие, без времени DNS.
    public static async Task<long?> PingAsync(string host, int port, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(Timeout);
        try
        {
            var addresses = IPAddress.TryParse(host, out var ip) ? new[] { ip } : await DohResolver.ResolveAsync(host, budget.Token).ConfigureAwait(false);
            var address = addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).FirstOrDefault();
            if (address is null) return null;
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            var clock = Stopwatch.StartNew();
            await socket.ConnectAsync(new IPEndPoint(address, port), budget.Token).ConfigureAwait(false);
            return Math.Max(1, clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied) { return Blocked; }
        catch (SocketException) { return null; }
        // Имя не разрешилось ни DNS сети, ни DoH.
        catch (UserError) { return null; }
    }
}
