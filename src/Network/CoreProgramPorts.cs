using System.Net;
using System.Net.Sockets;

namespace Oblachka;

internal static class CoreProgramPorts
{
    // Same lease format for older services. Updated cores use the adjacent port
    // for compact statistics; the client checks that both listeners share a PID.
    internal static CoreStatsEntry Create()
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var primary = new TcpListener(IPAddress.Loopback, 0);
            TcpListener? programs = null;
            try
            {
                primary.Start();
                var port = ((IPEndPoint)primary.LocalEndpoint).Port;
                if (port == 65535) continue;
                programs = new TcpListener(IPAddress.Loopback, port + 1);
                programs.Start();
                return CoreStatsEntry.Create(port);
            }
            catch (SocketException) { }
            finally { programs?.Stop(); primary.Stop(); }
        }
        throw new UserError("Не удалось выбрать локальные порты статистики ядра.");
    }
}
