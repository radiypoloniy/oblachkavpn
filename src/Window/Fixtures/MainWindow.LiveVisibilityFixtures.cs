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
            window.Hide();
            if (window.liveTimer?.IsEnabled != false) throw new InvalidOperationException("Hidden live view retained its timer");
            window.Show(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (window.liveTimer?.IsEnabled != true) throw new InvalidOperationException("Returning from tray did not resume connection polling");
            window.WindowState = WindowState.Minimized;
            if (window.liveTimer?.IsEnabled != false) throw new InvalidOperationException("Minimized live view retained its timer");
            window.WindowState = WindowState.Normal;
            if (window.liveTimer?.IsEnabled != true) throw new InvalidOperationException("Restoring the window did not resume connection polling");
            checks.Add("Connection polling stops in tray/minimized windows, resumes on restore and does not scan apps before showing");
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
