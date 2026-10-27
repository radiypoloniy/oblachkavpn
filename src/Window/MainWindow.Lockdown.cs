namespace Oblachka;

// «Без VPN — без интернета» (Mullvad Lockdown, Proton Permanent kill switch). Запрет ставит служба
// (WfpLockdown), окно только просит. Просит и при каждом запуске: установщик при обновлении снимает
// запрет вместе со старой службой, а локальная сеть могла смениться, пока окно было закрыто.
public partial class MainWindow
{
    private bool lockdownBusy;

    private void WireLockdown()
    {
        AppTab.Lockdown.Click += async (_, _) =>
        {
            var on = AppTab.Lockdown.IsChecked == true;
            if (on && !AppDialog.Confirm(this, "Закрыть интернет без VPN?",
                    "Пока VPN выключен, программы не выйдут в интернет — даже после выхода из Облачки и перезагрузки. " +
                    "Облачка сама останется на связи, чтобы подключиться. Снять запрет — этим же переключателем или удалив Облачку.", "Закрыть"))
            { AppTab.Lockdown.IsChecked = false; return; }
            await SendLockdownAsync(on);
            QueueStateSave();
        };
        Settings.LocalNetwork.Click += async (_, _) => { if (AppTab.Lockdown.IsChecked == true) await SendLockdownAsync(true); };
    }

    // false — служба не ответила или отказала: переключатель возвращается к тому, что есть на деле.
    private async Task SendLockdownAsync(bool on)
    {
        if (lockdownBusy) return;
        lockdownBusy = true;
        IHelperLifetime? temporary = null;
        try
        {
            var client = helperLifetime?.Client;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            if (client is null) { temporary = await NetworkHelperConnection.ConnectAsync(timeout.Token); client = temporary.Client; }
            var blocking = await client.LockdownAsync(on, Settings.LocalNetwork.IsChecked == true, timeout.Token);
            AppTab.Lockdown.IsChecked = blocking;
            if (blocking != on) DetailText.Text = on ? "Служба не закрыла интернет без VPN." : "Служба не сняла запрет без VPN.";
        }
        catch (Exception ex) when (ex is UserError or IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            AppTab.Lockdown.IsChecked = !on;
            DetailText.Text = (on ? "Не удалось закрыть интернет без VPN: " : "Не удалось снять запрет без VPN: ") + ex.Message;
        }
        finally
        {
            if (temporary is not null) await temporary.DisposeAsync();
            lockdownBusy = false;
        }
    }
}
