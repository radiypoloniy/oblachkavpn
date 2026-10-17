using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Oblachka;

// Соединения компьютера прямо сейчас — из таблиц Windows (GetExtendedTcpTable/UdpTable): кто,
// куда, с какого адреса. Ядро и служба для этого не нужны, права администратора тоже.
// Через VPN или мимо, видно по адресу самого соединения: в «Весь компьютер» всё, что идёт в
// туннель, открыто с адреса туннеля (SingBoxTunConfig.TunHost4/6).
public sealed record LiveSocket(int Pid, bool Udp, IPAddress Local, int LocalPort, IPAddress? Remote, int RemotePort, int State);

public static class ConnectionTable
{
    private const int AfInet = 2, AfInet6 = 23, TcpOwnerPidAll = 5, UdpOwnerPid = 1;
    public const int Established = 5, Listening = 2;

    public static IReadOnlyList<LiveSocket> Read()
    {
        var result = new List<LiveSocket>();
        foreach (var family in new[] { AfInet, AfInet6 })
        {
            Table(family, udp: false, result);
            Table(family, udp: true, result);
        }
        return result;
    }

    private static void Table(int family, bool udp, List<LiveSocket> result)
    {
        var size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(Math.Max(size, 4));
            try
            {
                var code = udp ? GetExtendedUdpTable(buffer, ref size, false, family, UdpOwnerPid, 0) : GetExtendedTcpTable(buffer, ref size, false, family, TcpOwnerPidAll, 0);
                if (code == 122) continue; // ERROR_INSUFFICIENT_BUFFER: таблица выросла, повторяем с новым размером.
                if (code != 0) return;
                Parse(buffer, family, udp, result);
                return;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    // Строки таблиц: IPv4 TCP — состояние, адрес, порт, адрес, порт, PID (по 4 байта); IPv6 TCP —
    // адрес 16, scope 4, порт 4, адрес 16, scope 4, порт 4, состояние, PID; UDP — без удалённой стороны.
    private static void Parse(IntPtr buffer, int family, bool udp, List<LiveSocket> result)
    {
        var count = Marshal.ReadInt32(buffer);
        var v6 = family == AfInet6;
        var rowSize = udp ? (v6 ? 28 : 12) : (v6 ? 56 : 24);
        var row = buffer + 4;
        for (var i = 0; i < Math.Min(count, 100_000); i++, row += rowSize)
        {
            if (udp)
            {
                var local = v6 ? Address(row, 16) : Address(row, 4);
                var port = Port(row + (v6 ? 20 : 4));
                result.Add(new(Marshal.ReadInt32(row + (v6 ? 24 : 8)), true, local, port, null, 0, 0));
            }
            else if (v6)
                result.Add(new(Marshal.ReadInt32(row + 52), false, Address(row, 16), Port(row + 20), Address(row + 24, 16), Port(row + 44), Marshal.ReadInt32(row + 48)));
            else
                result.Add(new(Marshal.ReadInt32(row + 20), false, Address(row + 4, 4), Port(row + 8), Address(row + 12, 4), Port(row + 16), Marshal.ReadInt32(row)));
        }
    }

    private static IPAddress Address(IntPtr at, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(at, bytes, 0, length);
        return new IPAddress(bytes);
    }

    // Порт хранится в сетевом порядке в младших двух байтах.
    private static int Port(IntPtr at) => (Marshal.ReadByte(at) << 8) | Marshal.ReadByte(at + 1);

    // Путь программы по PID без прав администратора: ограниченного доступа хватает и для служб.
    public static string? ProcessPath(int pid)
    {
        if (pid <= 4) return null;
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var text = new StringBuilder(1024);
            var length = text.Capacity;
            return QueryFullProcessImageName(handle, 0, text, ref length) ? text.ToString(0, length) : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
