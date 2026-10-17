using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Oblachka;

// Новое правило программы ядро применяет к новым соединениям. Telegram, Steam, Discord держат
// свои соединения часами — и шли бы прежним путём, будто правило не сработало. Поэтому служба
// после смены правил обрывает открытые TCP-соединения тех программ, чьё правило поменялось:
// программа сразу переподключается, уже по новому правилу. Нужны права службы (SetTcpEntry).
// Windows умеет так только с IPv4; соединения IPv6 и UDP доживают прежним путём.
public static class ConnectionReset
{
    private const int DeleteTcb = 12;

    public static int Apply(IReadOnlyList<ProgramRule> before, IReadOnlyList<ProgramRule> after)
    {
        var reset = 0;
        var paths = new Dictionary<int, string?>();
        foreach (var socket in ConnectionTable.Read())
        {
            if (socket.Udp || socket.State != ConnectionTable.Established || socket.Remote is null ||
                socket.Local.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(socket.Remote)) continue;
            if (!paths.TryGetValue(socket.Pid, out var path)) paths[socket.Pid] = path = ConnectionTable.ProcessPath(socket.Pid);
            if (path is null || !Changed(before, after, path)) continue;
            var row = new TcpRow
            {
                State = DeleteTcb,
                LocalAddr = Address(socket.Local), LocalPort = Port(socket.LocalPort),
                RemoteAddr = Address(socket.Remote), RemotePort = Port(socket.RemotePort)
            };
            if (SetTcpEntry(ref row) == 0) reset++;
        }
        return reset;
    }

    internal static bool Changed(IReadOnlyList<ProgramRule> before, IReadOnlyList<ProgramRule> after, string path) =>
        ProgramRoutingPlan.Find(before, path)?.Action != ProgramRoutingPlan.Find(after, path)?.Action;

    // Адрес и порт — в сетевом порядке байт, как в таблице соединений.
    private static uint Address(IPAddress address) => BitConverter.ToUInt32(address.GetAddressBytes(), 0);
    private static uint Port(int port) => (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint SetTcpEntry(ref TcpRow row);
}
