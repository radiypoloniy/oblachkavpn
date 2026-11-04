using System.Windows;
using System.Windows.Threading;

namespace Oblachka;

public partial class MainWindow
{
    private async Task CheckLiveVisibilityAsync(List<string> checks)
    {
        using var owner = new ClientRuntime(new WindowStartCore());
        owner.Persistence.Configure(null, isolated: true); owner.Activity.VmTestActive = true;
        owner.Preferences.StayInTray = false;
        var window = new MainWindow(owner, persistenceEnabled: false) { ShowActivated = false };
        try
        {
            window.PrepareResourceProfiles(2);
            if (window.appCatalog is not null || window.appCatalogLoading || window.liveTimer?.IsEnabled == true)
                throw new InvalidOperationException("An invisible window started catalog scanning or connection polling");
            // Synthetic catalog: verification does not enumerate the user's installed programs.
            window.appCatalog = [];
            window.Show(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (window.liveTimer?.IsEnabled != true) throw new InvalidOperationException("Visible live view did not start polling");
            if (!window.presentationTimer.IsEnabled || window.presentationTimer.Interval != VisiblePresentationInterval)
                throw new InvalidOperationException("Visible presentation did not use its normal cadence");
            const string game = @"C:\Games\game.exe";
            CoreConnection[] core = [new(game, false, "voice.example.net", "192.0.2.1", 443, [SingBoxTunConfig.UserDirect], "voice")];
            var live = window.LiveRow(LiveApps.FromCore(core, _ => false).Single());
            live.Expanded = true;
            live.Sites.Single().Expanded = true;
            window.liveObservation = new(new DateTime(2026, 10, 21, 12, 30, 0), core);
            window.RefreshLiveDetails();
            if (!live.Observation.Contains("Ядро туннеля") || !live.Sites.Single().Evidence.Contains("Ядро наблюдало — прямой выход"))
                throw new InvalidOperationException("Expanded connection details lost the snapshot source or route");
            window.liveObservation = new(new DateTime(2026, 10, 21, 12, 30, 1), null);
            live.ShowRoute(0, 0, 1);
            window.RefreshLiveDetails();
            if (!live.Observation.Contains("Таблицы Windows") || !live.Observation.Contains("Маршрут неизвестен") || live.Sites.Single().Evidence.Length != 0)
                throw new InvalidOperationException("Windows fallback reused the previous core observation");
            window.Hide();
            if (window.liveTimer?.IsEnabled != false) throw new InvalidOperationException("Hidden live view retained its timer");
            if (window.liveObservation is not null) throw new InvalidOperationException("Hidden live view retained its observation");
            if (window.presentationTimer.IsEnabled)
                throw new InvalidOperationException("Hidden window retained its presentation timer");
            window.runtime.Zapret.Restore(true, defer: true);
            if (window.presentationTimer.IsEnabled)
                throw new InvalidOperationException("Zapret resumed the hidden presentation timer");
            window.runtime.Zapret.Restore(false, defer: false);
            window.Show(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (window.liveTimer?.IsEnabled != true) throw new InvalidOperationException("Returning from tray did not resume connection polling");
            window.WindowState = WindowState.Minimized;
            if (window.liveTimer?.IsEnabled != false) throw new InvalidOperationException("Minimized live view retained its timer");
            window.WindowState = WindowState.Normal;
            if (window.liveTimer?.IsEnabled != true) throw new InvalidOperationException("Restoring the window did not resume connection polling");
            checks.Add("Connection polling stops in tray/minimized windows, resumes on restore and does not scan apps before showing");
            checks.Add("Expanded connection details distinguish core observations from Windows fallback and release the snapshot on hide");
            checks.Add("A hidden window stops its presentation timer even when Zapret is enabled");
            var nodes = window.importedProfiles;
            var second = Profiles.Parse("vless://22222222-2222-4222-8222-222222222222@node0.invalid:443?security=tls&type=tcp#Second");
            window.SetGroup(ProfileGroup.Manual, [nodes[0], second]); window.ApplyProfiles(false); window.RefreshHome();
            if (window.Home.AllServersText.Text != "1") throw new InvalidOperationException("Machine count did not merge protocols of the same host");
            window.PrepareResourceProfiles(3); window.RefreshHome();
            if (window.Home.AllServersText.Text != "3") throw new InvalidOperationException("Cached machine count survived replacing the imported list");
            checks.Add("Cached server count merges protocols and refreshes when the imported profile list changes");
        }
        finally { window.Close(); }
    }
}
