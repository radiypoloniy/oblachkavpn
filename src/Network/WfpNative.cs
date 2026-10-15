using System.Runtime.InteropServices;

namespace Oblachka;

// Объявления fwpuclnt.dll. Раскладка структур повторяет fwpmtypes.h для x64: сборка
// только x64 (Oblachka.csproj), поэтому выравнивание указателей — 8 байт. Размеры
// закреплены проверкой (Filter — 200 байт, Condition — 40), иначе ошибка раскладки
// проявилась бы только отказом WFP на живой системе.
internal static class WfpNative
{
    private const string Dll = "fwpuclnt.dll";

    [StructLayout(LayoutKind.Sequential)]
    internal struct Display { public IntPtr Name; public IntPtr Description; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Blob { public uint Size; public IntPtr Data; }

    // FWP_VALUE0 и FWP_CONDITION_VALUE0: тип и восьмибайтное объединение. Малые числа
    // лежат в младших байтах объединения, то есть совпадают с ulong на little-endian.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Value { public uint Type; public ulong Data; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Session
    {
        public Guid Key; public Display Display; public uint Flags; public uint TxnWaitTimeout; public uint ProcessId;
        public IntPtr Sid; public IntPtr UserName; public int KernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Provider { public Guid Key; public Display Display; public uint Flags; public Blob Data; public IntPtr ServiceName; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Sublayer { public Guid Key; public Display Display; public uint Flags; public IntPtr ProviderKey; public Blob Data; public ushort Weight; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Condition { public Guid Field; public uint Match; public Value Value; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Action { public uint Type; public Guid FilterType; }

    // Объединение rawContext/providerContextKey выровнено по 8 байтам. Guid в .NET
    // выровнен по 4, поэтому объединение описано двумя ulong — иначе всё после него
    // съехало бы на 4 байта.
    [StructLayout(LayoutKind.Sequential)]
    internal struct Context { public ulong Low; public ulong High; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Filter
    {
        public Guid Key; public Display Display; public uint Flags; public IntPtr ProviderKey; public Blob ProviderData;
        public Guid LayerKey; public Guid SublayerKey; public Value Weight; public uint ConditionCount; public IntPtr Conditions;
        public Action Action; public Context Context; public IntPtr Reserved; public ulong FilterId; public Value EffectiveWeight;
    }

    [DllImport(Dll)] internal static extern uint FwpmEngineOpen0(IntPtr serverName, uint authnService, IntPtr authIdentity, ref Session session, out IntPtr engine);
    [DllImport(Dll)] internal static extern uint FwpmEngineClose0(IntPtr engine);
    [DllImport(Dll)] internal static extern uint FwpmTransactionBegin0(IntPtr engine, uint flags);
    [DllImport(Dll)] internal static extern uint FwpmTransactionCommit0(IntPtr engine);
    [DllImport(Dll)] internal static extern uint FwpmTransactionAbort0(IntPtr engine);
    [DllImport(Dll)] internal static extern uint FwpmProviderAdd0(IntPtr engine, ref Provider provider, IntPtr sd);
    [DllImport(Dll)] internal static extern uint FwpmProviderDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport(Dll)] internal static extern uint FwpmSubLayerAdd0(IntPtr engine, ref Sublayer sublayer, IntPtr sd);
    [DllImport(Dll)] internal static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport(Dll)] internal static extern uint FwpmSubLayerGetByKey0(IntPtr engine, ref Guid key, out IntPtr sublayer);
    [DllImport(Dll)] internal static extern uint FwpmFilterAdd0(IntPtr engine, ref Filter filter, IntPtr sd, out ulong id);
    [DllImport(Dll)] internal static extern uint FwpmFilterDeleteByKey0(IntPtr engine, ref Guid key);
    [DllImport(Dll, CharSet = CharSet.Unicode)] internal static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);
    [DllImport(Dll)] internal static extern void FwpmFreeMemory0(ref IntPtr memory);
}
