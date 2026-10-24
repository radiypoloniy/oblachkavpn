namespace Oblachka;

// Выходной IP и страна (ExitAddress): после проверки связи — во второй строке карточки сервера
// («выход — Германия, 185.…»; страна первой, чтобы обрезался хвост адреса, а не она) и в подсказке
// к карточке. Узнаётся один раз на сервер и ядро; в «Через VPN только выбранное» не спрашиваем —
// адрес там зависит от сайта.
public partial class MainWindow
{
    private ExitInfo? exitInfo;
    private string? exitFor;

    private void RefreshExit(bool checkedOk)
    {
        var key = checkedOk && runningProfile is not null ? FavoriteKey(runningProfile) + ":" + presentedPid + ":" + SystemTunnelActive : null;
        if (key == exitFor) return;
        exitFor = key; exitInfo = null;
        if (key is not null && !(SystemTunnelActive && selectiveVpn)) _ = FetchExitAsync(key);
    }

    private string? ExitNote(Profile shown)
    {
        var exit = exitInfo is not null && runningProfile is not null && ReferenceEquals(shown, runningProfile) ? exitInfo : null;
        Home.ServerCard.ToolTip = exit is null ? null : "Сайты видят адрес " + exit.Text;
        return exit is null ? null : "выход — " + (exit.Country.Code is null ? "" : exit.Country.Name + ", ") + exit.Address;
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
