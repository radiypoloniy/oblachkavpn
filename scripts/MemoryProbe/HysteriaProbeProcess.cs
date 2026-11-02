using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

internal sealed class HysteriaProbeProcess : IAsyncDisposable
{
    internal Process Process { get; }
    private readonly Task<string> output, error;
    private HysteriaProbeProcess(Process process)
    { Process = process; output = process.StandardOutput.ReadToEndAsync(); error = process.StandardError.ReadToEndAsync(); }
    internal static async Task<HysteriaProbeProcess> StartAsync(string executable, JsonObject config, CancellationToken token)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "run", "-c", "stdin" }) start.ArgumentList.Add(arg);
        var core = new HysteriaProbeProcess(Process.Start(start)!);
        try { await core.Process.StandardInput.WriteAsync(config.ToJsonString().AsMemory(), token); core.Process.StandardInput.Close(); return core; }
        catch { await core.DisposeAsync(); throw; }
    }
    internal static int Port(bool udp = false)
    {
        if (udp) { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    internal async Task ReadyAsync(int port, CancellationToken token)
    {
        for (var i = 0; i < 100; i++)
        {
            if (Process.HasExited) throw new InvalidOperationException("Core exited during fixture startup");
            try { using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port, token); return; }
            catch (SocketException) { await Task.Delay(50, token); }
        }
        throw new TimeoutException("Fixture core did not become ready");
    }
    internal CoreMemory Memory()
    {
        Process.Refresh();
        if (!GetProcessMemoryInfo(Process.Handle, out var counters, (uint)Marshal.SizeOf<Counters>()))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new(Process.PrivateMemorySize64, (long)counters.PrivateWorkingSetSize, Process.WorkingSet64, Process.TotalProcessorTime.TotalMilliseconds);
    }
    public async ValueTask DisposeAsync()
    {
        if (!Process.HasExited) { Process.Kill(); await Process.WaitForExitAsync(); }
        await Task.WhenAll(output, error); Process.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out Counters counters, uint size);
}
internal sealed record CoreMemory(long PrivateBytes, long PrivateWorkingSet, long WorkingSet, double CpuMs);
