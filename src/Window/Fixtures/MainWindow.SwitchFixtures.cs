using System.Windows;
using System.Windows.Threading;

namespace Oblachka;

// Смена сервера на ходу в обычном режиме: выбор другого сервера при работающем
// подключении переключает ядро сам, без «Остановить» и «Подключиться».
public partial class MainWindow
{
    private async Task CheckLiveServerSwitch(List<string> checks)
    {
        ProfilePicker.SelectedIndex = 0;
        await ConnectCoreAsync();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var before = engine.ProcessId;
        if (engine.State != ConnectionState.Running || before is null) throw new InvalidOperationException("Switch fixture did not connect");
        if (!ProfilePicker.IsEnabled) throw new InvalidOperationException("Server list is locked while connected");

        var target = (Profile)ProfilePicker.Items[1];
        ProfilePicker.SelectedIndex = 1;
        await Task.Delay(1500);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (engine.State != ConnectionState.Running || engine.ProcessId is null || engine.ProcessId == before ||
            runningProfile is null || Profiles.Fingerprint(runningProfile) != Profiles.Fingerprint(target))
            throw new InvalidOperationException("Selecting another server while connected did not switch to it");

        // Повторный выбор того же сервера ничего не перезапускает.
        var switched = engine.ProcessId;
        ProfilePicker.SelectedIndex = 1;
        await Task.Delay(1000);
        if (engine.ProcessId != switched) throw new InvalidOperationException("Reselecting the running server restarted the core");

        StopClick(this, new RoutedEventArgs());
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        if (engine.ProcessId is not null) throw new InvalidOperationException("Stop after a live switch left a running core");
        checks.Add("Choosing another server while connected switches to it without Stop/Connect");
    }
}
