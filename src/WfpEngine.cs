using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Oblachka;

public enum WfpConditionKind { Loopback, LocalAddress, RemoteAddress, Application, Protocol, LocalPort, RemotePort, InterfaceTypeNot }
public sealed record WfpCondition(WfpConditionKind Kind, IPAddress? Address = null, string? Program = null, int Number = 0);
// Packet — слой исходящих IP-пакетов, а не установления соединений: там видны пакеты
// уже разрешённых потоков, в том числе закрытие соединения после снятия туннеля.
public sealed record WfpFilter(Guid Key, string Name, bool V6, byte Weight, bool Permit, WfpCondition[] Conditions, bool Packet = false);

// Windows Filtering Platform, каким её видит защита. Интерфейс нужен затем, чтобы порядок
// и состав фильтров проверялись на подменном движке, а не на сети компьютера.
public interface IWfpEngine : IDisposable
{
    void Begin();
    void Commit();
    void Abort();
    bool SublayerExists(Guid key);
    void AddProvider(Guid key, string name);
    void AddSublayer(Guid key, Guid provider, string name, ushort weight);
    void AddFilter(WfpFilter filter, Guid provider, Guid sublayer);
    // false — объекта не было. Это нужный итог снятия, а не ошибка.
    bool DeleteFilter(Guid key);
    bool DeleteSublayer(Guid key);
    bool DeleteProvider(Guid key);
}

// Движок WFP через fwpuclnt.dll. Сессия не динамическая: фильтры не исчезают вместе с
// процессом помощника, иначе его падение сняло бы защиту. Снимает их сессия по журналу,
// в том числе при следующем запуске помощника. Флаг постоянства не ставится: после
// перезагрузки фильтров нет, и компьютер не остаётся без сети из-за забытой сессии.
public sealed class WfpEngine : IWfpEngine
{
    // Ключи из fwpmu.h. Сверены на стенде: netsh wfp show filters называет наши слои и
    // условия их символическими именами, а это возможно только при верных ключах.
    public static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    public static readonly Guid LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    public static readonly Guid LayerPacketV4 = new("1e5c9fae-8a84-4135-a331-950b54229ecd");
    public static readonly Guid LayerPacketV6 = new("a3b3ab6b-3564-488c-9117-f34e82142763");
    private static readonly Guid ConditionInterfaceType = new("daf8cd14-e09e-4c93-a5ae-c5c13b73ffca");
    private static readonly Guid ConditionFlags = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private static readonly Guid ConditionLocalAddress = new("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
    private static readonly Guid ConditionRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid ConditionProtocol = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7");
    private static readonly Guid ConditionLocalPort = new("0c1ba1af-5765-453f-af22-a8f791ac775b");
    private static readonly Guid ConditionRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");
    private static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");

    private const uint RpcAuthnWinNt = 10;
    private const uint ActionBlock = 0x1001, ActionPermit = 0x1002;
    // Разрешение с очищенным правом на действие не может перебить подслой ниже по весу —
    // в том числе брандмауэр Windows, если в нём исходящее по умолчанию запрещено.
    private const uint FlagClearActionRight = 0x8;
    private const uint MatchEqual = 0, MatchFlagsAllSet = 6, MatchNotEqual = 10;
    private const uint TypeUint8 = 1, TypeUint16 = 2, TypeUint32 = 3, TypeArray16 = 11, TypeBlob = 12;
    private const uint IsLoopback = 0x1;
    private const uint FilterNotFound = 0x80320003, ProviderNotFound = 0x80320005, SublayerNotFound = 0x80320007;

    private IntPtr engine;

    private WfpEngine(IntPtr handle) => engine = handle;

    public static WfpEngine Open()
    {
        var session = new WfpNative.Session();
        var status = WfpNative.FwpmEngineOpen0(IntPtr.Zero, RpcAuthnWinNt, IntPtr.Zero, ref session, out var handle);
        if (status != 0) throw new IOException("Служба фильтрации Windows (BFE) недоступна: 0x" + status.ToString("X8"));
        return new WfpEngine(handle);
    }

    public void Begin() => Check(WfpNative.FwpmTransactionBegin0(engine, 0), "начало транзакции");
    public void Commit() => Check(WfpNative.FwpmTransactionCommit0(engine), "подтверждение транзакции");
    public void Abort() => WfpNative.FwpmTransactionAbort0(engine);

    public bool SublayerExists(Guid key)
    {
        var status = WfpNative.FwpmSubLayerGetByKey0(engine, ref key, out var found);
        if (found != IntPtr.Zero) WfpNative.FwpmFreeMemory0(ref found);
        if (status == SublayerNotFound) return false;
        Check(status, "поиск подслоя");
        return true;
    }

    public void AddProvider(Guid key, string name)
    {
        using var text = new Strings();
        var provider = new WfpNative.Provider { Key = key, Display = new() { Name = text.Add(name) } };
        Check(WfpNative.FwpmProviderAdd0(engine, ref provider, IntPtr.Zero), "добавление поставщика");
    }

    public void AddSublayer(Guid key, Guid provider, string name, ushort weight)
    {
        using var text = new Strings();
        var providerKey = text.Copy(provider);
        var sublayer = new WfpNative.Sublayer { Key = key, Display = new() { Name = text.Add(name) }, ProviderKey = providerKey, Weight = weight };
        Check(WfpNative.FwpmSubLayerAdd0(engine, ref sublayer, IntPtr.Zero), "добавление подслоя");
    }

    public void AddFilter(WfpFilter filter, Guid provider, Guid sublayer)
    {
        using var memory = new Strings();
        var conditions = filter.Conditions.Select(c => Condition(c, memory)).ToArray();
        var size = Marshal.SizeOf<WfpNative.Condition>();
        var array = memory.Alloc(Math.Max(1, conditions.Length) * size);
        for (var i = 0; i < conditions.Length; i++) Marshal.StructureToPtr(conditions[i], array + i * size, false);
        var native = new WfpNative.Filter
        {
            Key = filter.Key,
            Display = new() { Name = memory.Add(filter.Name) },
            Flags = filter.Permit ? FlagClearActionRight : 0,
            ProviderKey = memory.Copy(provider),
            LayerKey = filter.Packet ? (filter.V6 ? LayerPacketV6 : LayerPacketV4) : (filter.V6 ? LayerConnectV6 : LayerConnectV4),
            SublayerKey = sublayer,
            Weight = new() { Type = TypeUint8, Data = filter.Weight },
            ConditionCount = (uint)conditions.Length,
            Conditions = conditions.Length == 0 ? IntPtr.Zero : array,
            Action = new() { Type = filter.Permit ? ActionPermit : ActionBlock }
        };
        Check(WfpNative.FwpmFilterAdd0(engine, ref native, IntPtr.Zero, out _), "добавление фильтра «" + filter.Name + "»");
    }

    public bool DeleteFilter(Guid key) => Deleted(WfpNative.FwpmFilterDeleteByKey0(engine, ref key), FilterNotFound, "снятие фильтра");
    public bool DeleteSublayer(Guid key) => Deleted(WfpNative.FwpmSubLayerDeleteByKey0(engine, ref key), SublayerNotFound, "снятие подслоя");
    public bool DeleteProvider(Guid key) => Deleted(WfpNative.FwpmProviderDeleteByKey0(engine, ref key), ProviderNotFound, "снятие поставщика");

    private static WfpNative.Condition Condition(WfpCondition c, Strings memory) => c.Kind switch
    {
        WfpConditionKind.Loopback => new() { Field = ConditionFlags, Match = MatchFlagsAllSet, Value = new() { Type = TypeUint32, Data = IsLoopback } },
        WfpConditionKind.LocalAddress => Address(ConditionLocalAddress, c.Address!, memory),
        WfpConditionKind.RemoteAddress => Address(ConditionRemoteAddress, c.Address!, memory),
        WfpConditionKind.Protocol => new() { Field = ConditionProtocol, Value = new() { Type = TypeUint8, Data = (ulong)c.Number } },
        WfpConditionKind.LocalPort => new() { Field = ConditionLocalPort, Value = new() { Type = TypeUint16, Data = (ulong)c.Number } },
        WfpConditionKind.RemotePort => new() { Field = ConditionRemotePort, Value = new() { Type = TypeUint16, Data = (ulong)c.Number } },
        WfpConditionKind.InterfaceTypeNot => new() { Field = ConditionInterfaceType, Match = MatchNotEqual, Value = new() { Type = TypeUint32, Data = (ulong)c.Number } },
        WfpConditionKind.Application => new() { Field = ConditionAppId, Value = new() { Type = TypeBlob, Data = (ulong)memory.AppId(c.Program!) } },
        _ => throw new UserError("Неизвестное условие фильтра.")
    };

    // IPv4 в WFP — число в порядке байт узла, IPv6 — указатель на 16 байт.
    private static WfpNative.Condition Address(Guid field, IPAddress address, Strings memory)
    {
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return new() { Field = field, Match = MatchEqual, Value = new() { Type = TypeUint32, Data = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]) } };
        var buffer = memory.Alloc(16);
        Marshal.Copy(bytes, 0, buffer, 16);
        return new() { Field = field, Match = MatchEqual, Value = new() { Type = TypeArray16, Data = (ulong)buffer } };
    }

    private static bool Deleted(uint status, uint notFound, string what)
    {
        if (status == notFound) return false;
        Check(status, what);
        return true;
    }

    private static void Check(uint status, string what)
    {
        if (status != 0) throw new IOException("Фильтрация Windows отказала: " + what + ", 0x" + status.ToString("X8"));
    }

    public void Dispose()
    {
        if (engine == IntPtr.Zero) return;
        WfpNative.FwpmEngineClose0(engine);
        engine = IntPtr.Zero;
    }

    // Неуправляемая память одного вызова: строки, ключи, адреса и идентификаторы программ.
    private sealed class Strings : IDisposable
    {
        private readonly List<IntPtr> owned = new();
        private readonly List<IntPtr> wfp = new();
        public IntPtr Add(string text) { var p = Marshal.StringToHGlobalUni(text); owned.Add(p); return p; }
        public IntPtr Alloc(int bytes) { var p = Marshal.AllocHGlobal(bytes); owned.Add(p); return p; }
        public IntPtr Copy(Guid key) { var p = Alloc(16); Marshal.StructureToPtr(key, p, false); return p; }
        public IntPtr AppId(string program)
        {
            var status = WfpNative.FwpmGetAppIdFromFileName0(program, out var blob);
            if (status != 0) throw new IOException("Фильтрация Windows не опознала программу ядра: 0x" + status.ToString("X8"));
            wfp.Add(blob);
            return blob;
        }
        public void Dispose()
        {
            foreach (var p in owned) Marshal.FreeHGlobal(p);
            for (var i = 0; i < wfp.Count; i++) { var p = wfp[i]; WfpNative.FwpmFreeMemory0(ref p); }
        }
    }
}
