namespace Oblachka;

// Выходной IP и страна (ExitAddress): после проверки связи — код страны в отметке «Связь проверена»,
// адрес и страна в подсказке к ней. Узнаётся один раз на сервер и ядро; в «Через VPN только
// выбранное» не спрашиваем — адрес там зависит от сайта.
public partial class MainWindow
{
    private ExitInfo? exitInfo;
    private string? exitFor;

    private void RefreshExit(bool checkedOk)
    {
        var key = checkedOk && runningProfile is not null ? FavoriteKey(runningProfile) + ":" + presentedPid + ":" + SystemTunnelActive : null;
        if (key != exitFor)
        {
            exitFor = key; exitInfo = null;
            if (key is not null && !(SystemTunnelActive && selectiveVpn)) _ = FetchExitAsync(key);
        }
        if (exitInfo is { } exit)
        {
            Home.CheckText.Text += " · " + (exit.Country.Code ?? exit.Address.ToString());
            Home.CheckChip.ToolTip = "Сайты видят адрес " + exit.Text;
        }
        else Home.CheckChip.ToolTip = checkedOk && SystemTunnelActive && selectiveVpn ? "Через VPN идут только выбранные сайты и программы" : null;
    }

    private async Task FetchExitAsync(string key)
    {
        var port = SystemTunnelActive ? null : (engine as Engine)?.Port;
        if (!SystemTunnelActive && port is null) return;
        var exit = await ExitAddress.FetchAsync(port, CancellationToken.None);
        if (exitFor != key) return;
        exitInfo = exit;
        RefreshHome();
    }
}
