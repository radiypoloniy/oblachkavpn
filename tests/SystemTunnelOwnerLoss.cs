using System.Diagnostics;

namespace Oblachka;

// Сценарий «окно погибло» при сетевой службе. Ради него служба и нужна: помощник с UAC
// снимал защиту вместе с окном, служба обязана её удержать. Владелец — отдельный процесс
// (--owner-fixture), его убивают; сеть должна остаться закрытой, пока следующий владелец
// не попросит восстановить её (кнопка «Восстановить сеть»).
internal static class SystemTunnelOwnerLoss
{
    internal static void Run(TunnelEndpointPlan plan, Action<string> add, Func<string> probe)
    {
        var ready = Path.Combine(Path.GetTempPath(), "oblachka-owner-" + Guid.NewGuid().ToString("N"));
        var start = new ProcessStartInfo(TestHost.Executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "--owner-fixture", plan.Peers[0], ready }) start.ArgumentList.Add(argument);
        Process? owner = null;
        try
        {
            owner = Process.Start(start)!;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(ready) && !owner.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(200);
            if (!File.Exists(ready)) { add("смерть окна: владелец не поднял сессию (код " + (owner.HasExited ? owner.ExitCode : -1) + ")"); return; }
            add("смерть окна: сессия владельца поднята, " + WfpStateProbe.Describe().Split(';')[0]);
            owner.Kill(true); owner.WaitForExit(10000);
            Thread.Sleep(3000);
            add("смерть окна: журнал службы " + NetworkSessionJournal.PeekState(NetworkService.JournalPath) +
                ", " + WfpStateProbe.Describe().Split(';')[0] + ", запрос: " + probe());
            using (var token = new CancellationTokenSource(TimeSpan.FromMinutes(1)))
            {
                var lifetime = NetworkHelperConnection.ConnectAsync(token.Token).GetAwaiter().GetResult();
                var state = lifetime.Client.RecoverAsync(token.Token).GetAwaiter().GetResult();
                lifetime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                add("смерть окна: «Восстановить сеть» — " + state + ", " + WfpStateProbe.Describe().Split(';')[0] + ", выход: " + probe());
            }
        }
        catch (Exception ex) { add("смерть окна: сценарий прерван (" + ex.GetType().Name + (ex is UserError ? ": " + ex.Message : "") + ")"); }
        finally
        {
            try { if (owner is { HasExited: false }) owner.Kill(true); } catch (Exception) { }
            owner?.Dispose();
            try { File.Delete(ready); } catch (IOException) { }
        }
    }

    // Владелец без ядра: туннелю некуда вести трафик, но для проверки защиты это и нужно.
    internal static int Fixture(string peer, string ready)
    {
        using var token = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var lifetime = NetworkHelperConnection.ConnectAsync(token.Token).GetAwaiter().GetResult();
        if (!lifetime.IsService) return 3;
        lifetime.Client.AcquireAsync(new(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, new[] { peer }), token.Token).GetAwaiter().GetResult();
        File.WriteAllText(ready, "ok");
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }
}
