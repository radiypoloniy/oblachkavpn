using System.Windows;
using System.Windows.Threading;

namespace Oblachka;

public partial class MainWindow
{
    private async Task CheckDesktopHostAsync(List<string> checks)
    {
        var core = new ViewLifetimeCore();
        using var owner = new ClientRuntime(core);
        owner.Persistence.Configure(null, isolated: true);
        using var desktop = new ClientDesktop(Application.Current, owner);
        desktop.Start(hidden: false, link: null);
        var current = desktop.Window();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!current.IsVisible || !ReferenceEquals(current.runtime, owner) || owner.Tray?.Icon.Visible != true)
            throw new InvalidOperationException("Application host did not show its runtime-owned view and tray");
        if (current.Settings.SystemTunnel.IsEnabled || current.Settings.SystemProxy.IsEnabled)
            throw new InvalidOperationException("An isolated application host enabled system networking");
        current.exitRequested = true; desktop.Close();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!current.IsClosed || core.Disposed || owner.Tray?.Icon.Visible != true)
            throw new InvalidOperationException("Application host tied runtime/tray lifetime to its closed view");
        checks.Add("Application host opens an isolated view, reuses it and retains runtime/tray ownership after closing it");
    }
}
