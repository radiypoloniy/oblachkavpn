using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class ProcessMemory
{
    internal static object Read()
    {
        using var process = Process.GetCurrentProcess();
        if (!GetProcessMemoryInfo(process.Handle, out var counters, (uint)Marshal.SizeOf<Counters>()))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new { PrivateBytes = process.PrivateMemorySize64, PrivateWorkingSet = (long)counters.PrivateWorkingSetSize,
            WorkingSet = process.WorkingSet64, ManagedBytes = GC.GetTotalMemory(false) };
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage,
            PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out Counters counters, uint size);
}
