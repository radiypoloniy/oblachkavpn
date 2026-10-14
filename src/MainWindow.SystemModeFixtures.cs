using System.Windows;

namespace Oblachka;

public partial class MainWindow
{
    // Два системных режима одновременно не имеют смысла: прокси указывал бы на порт,
    // который уже забрал туннель. Проверяется, что переключатели гасят друг друга.
    private void CheckSystemModes(List<string> checks)
    {
        Settings.SystemProxy.IsChecked = true;
        UpdateModeText();
        Settings.SystemTunnel.IsChecked = true;
        SystemTunnelToggled(this, new RoutedEventArgs());
        if (Settings.SystemProxy.IsChecked == true)
            throw new InvalidOperationException("System VPN left the Windows proxy enabled at the same time");
        if (Connection.ModeTitle.Text != "Системный VPN")
            throw new InvalidOperationException("Main screen still claims the Windows proxy after switching to system VPN");
        checks.Add("System VPN and the Windows proxy cannot be enabled at the same time");

        // Пояснение режима обязано говорить о его границах, а не обещать полную защиту.
        // Границу эту дважды переписывала проверка на стенде: сначала требовалось слово
        // «UDP», пока его работа внутри туннеля не была доказана, потом — упоминание
        // трафика мимо туннеля, пока блокировки не было вовсе, потом — зависимости от
        // брандмауэра Windows. Теперь блокировка на фильтрах WFP, и её граница — срок
        // жизни приложения (без службы) или следующего запуска (со службой): о ней обязано
        // говорить пояснение в обоих случаях.
        var idle = Settings.SystemTunnelNote.Text;
        if (!idle.Contains("мимо") || !idle.Contains("заверш"))
            throw new InvalidOperationException("System mode note hides its limits: " + idle);
        UpdateSystemTunnelNote(SystemTunnelState.RecoveryRequired);
        if (!Settings.SystemTunnelNote.Text.Contains("не полностью"))
            throw new InvalidOperationException("Incomplete rollback is not reported to the user");
        UpdateSystemTunnelNote(SystemTunnelState.Running);
        if (!Settings.SystemTunnelNote.Text.Contains("весь трафик"))
            throw new InvalidOperationException("Running system mode is not described");
        // Сессии в этот момент нет, значит и блокировки нет: работающий режим обязан
        // это признать, а не выдавать защиту, которой не существует.
        if (!Settings.SystemTunnelNote.Text.Contains("не действует"))
            throw new InvalidOperationException("Running mode claims protection it does not have");
        checks.Add("System mode states and limits are shown plainly, including incomplete rollback");

        Settings.SystemTunnel.IsChecked = false;
        SystemTunnelToggled(this, new RoutedEventArgs());
        UpdateSystemTunnelNote(null);
    }
}
