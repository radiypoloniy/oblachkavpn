using System.Runtime.InteropServices;

namespace Oblachka;

// Лимитная ли сеть (раздача с телефона, мобильный модем): так её помечает Windows.
// Смотрим стоимость соединения физического адаптера (NetworkIdentity.Physical), а не
// общую по компьютеру: под «Весь компьютер» общим соединением Windows считает наш туннель,
// и лимитная сеть под ним выглядела бы безлимитной. Не вышло узнать — считаем безлимитной.
public static class NetworkCost
{
    private const uint Fixed = 0x2, Variable = 0x4, OverDataLimit = 0x10000, Roaming = 0x40000;

    public static bool IsMetered(uint cost) => (cost & (Fixed | Variable | OverDataLimit | Roaming)) != 0;

    public static bool Metered() => Cost() is { } cost && IsMetered(cost);

    // Флаги стоимости соединения физического адаптера; null — не нашлось или Windows не ответила.
    internal static uint? Cost()
    {
        if (NetworkIdentity.Physical() is not { } physical || !Guid.TryParse(physical.Adapter.Id, out var adapter)) return null;
        object? manager = null;
        try
        {
            manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!);
            var connections = ((INetworkListManager)manager!).GetNetworkConnections();
            while (connections.Next(1, out var connection, out var fetched) == 0 && fetched == 1)
            {
                try
                {
                    if (connection.GetAdapterId() == adapter && connection is INetworkConnectionCost cost)
                        return cost.GetCost();
                }
                finally { Marshal.ReleaseComObject(connection); }
            }
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return null; }
        finally { if (manager is not null) Marshal.ReleaseComObject(manager); }
    }

    // Порядок методов — как в таблице интерфейса; объявлены только нужные и предшествующие им.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsDual), Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork(Guid id);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsDual), Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B")]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNewEnum();
        [PreserveSig] int Next(uint count, out INetworkConnection connection, out uint fetched);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsDual), Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B")]
    private interface INetworkConnection
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork();
        short IsConnectedToInternet();
        short IsConnected();
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("DCB0000A-570F-4A9B-8D69-199FDBA5723B")]
    private interface INetworkConnectionCost
    {
        uint GetCost();
    }
}
