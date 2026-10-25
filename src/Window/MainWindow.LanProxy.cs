using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;

namespace Oblachka;

// Прокси для устройств локальной сети («Для продвинутых»): вход ядра туннеля на всех адресах
// (TunnelTuning.AddLan) с логином и паролем, порт в брандмауэре открывает служба (LanFirewall).
// Пароль создаёт приложение — придумывать его не нужно, а слабый не получится.
public partial class MainWindow
{
    private void WireLan()
    {
        Advanced.Extras.LanProxy.Click += (_, _) =>
        {
            var on = Advanced.Extras.LanProxy.IsChecked == true;
            SetAdvanced(advanced with { LanProxy = on, LanPassword = advanced.LanPassword ?? (on ? AdvancedOptions.NewLanPassword() : null) });
            FillLan();
        };
        Field(Advanced.Extras.LanPortInput, Advanced.Extras.LanPortError, text =>
        {
            if (Number(text, out var port) is { } bad) return bad;
            if (AdvancedOptions.CheckPort(port) is { } wrong) return wrong;
            if (port is not null && port == advanced.ProxyPort) return "Этот порт уже у прокси для программ.";
            SetAdvanced(advanced with { LanPort = port }); FillLan(); return null;
        });
        Advanced.Extras.LanNewPassword.Click += (_, _) =>
        {
            if (!AppDialog.Confirm(this, "Новый пароль прокси?", "Устройства со старым паролем перестанут подключаться — на них его нужно будет сменить.", "Сменить")) return;
            SetAdvanced(advanced with { LanPassword = AdvancedOptions.NewLanPassword() }); FillLan();
        };
        Advanced.Extras.LanCopy.Click += (_, _) =>
        {
            try { Clipboard.SetText("socks5://" + AdvancedOptions.LanUser + ":" + advanced.LanPassword + "@" + (LanIpv4() ?? "адрес-компьютера") + ":" + advanced.LanListenPort); DetailText.Text = "Ссылка прокси скопирована."; }
            catch (System.Runtime.InteropServices.COMException) { DetailText.Text = "Буфер обмена занят другой программой. Попробуйте ещё раз."; }
        };
    }

    private void FillLan()
    {
        Advanced.Extras.LanProxy.IsChecked = advanced.LanProxy;
        Advanced.Extras.LanDetails.Visibility = advanced.LanProxy ? Visibility.Visible : Visibility.Collapsed;
        Advanced.Extras.LanPortInput.Text = advanced.LanPort?.ToString() ?? "";
        Advanced.Extras.LanAddress.Text = (LanIpv4() ?? "IP этого компьютера") + ":" + advanced.LanListenPort;
        Advanced.Extras.LanCredentials.Text = "логин " + AdvancedOptions.LanUser + "   пароль " + advanced.LanPassword;
        Advanced.Extras.LanPortError.Visibility = Visibility.Collapsed;
    }

    // Адрес компьютера в локальной сети: IPv4 работающего интерфейса со шлюзом, не туннеля.
    private static string? LanIpv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    !n.Name.StartsWith(WindowsTunnelBackend.InterfacePrefix, StringComparison.Ordinal))
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                .SelectMany(p => p.UnicastAddresses).Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))?.ToString();
        }
        catch (NetworkInformationException) { return null; }
    }
}
