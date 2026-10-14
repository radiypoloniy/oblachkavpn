using System.Windows;
using System.Windows.Threading;

namespace Oblachka;

public partial class MainWindow
{
    private readonly DispatcherTimer presentationTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? sessionStarted;
    private DateTimeOffset? lastCheck;
    private ProbeResult? lastProbe;
    private int? presentedPid;
    private bool findingRoute;

    private void RefreshPresentation()
    {
        if (!viewsReady) return;
        var state = engine.State;
        var pid = engine.ProcessId;
        if (pid != presentedPid)
        {
            presentedPid = pid;
            sessionStarted = pid is null ? null : DateTimeOffset.UtcNow;
            lastProbe = null; lastCheck = null;
        }
        RefreshHome();
        Servers.SearchBox.IsEnabled = ProfilePicker.IsEnabled;
        RefreshServerListControls();
        Diagnostics.CoreState.Text = pid is null ? "Не запущен" : "Готов";
        Diagnostics.NetworkNote.Text = SystemTunnelActive
            ? "Включён системный режим. Запрос ниже проверяет связь через локальное ядро; проверка DNS, IPv6 и блокировки обхода выполняется отдельно."
            : "Запрос ниже проверяет связь через локальный прокси. Он не проверяет DNS, IPv6 и трафик остальных программ.";
        Diagnostics.TunnelState.Text = lastProbe is null ? "Не проверен" : lastProbe.Success ? $"{lastProbe.Milliseconds} мс" : "Не прошёл";
        Diagnostics.Heading.Text = lastProbe is { Success: true } ? "Туннель проверен" : "Проверим подключение";
        Diagnostics.LastCheck.Text = lastCheck is null ? "Проверка ещё не выполнялась" : "Последняя проверка " + lastCheck.Value.ToLocalTime().ToString("HH:mm:ss");
        Onboarding.ContinueButton.IsEnabled = operation is null;
        Onboarding.KeyInput.IsEnabled = operation is null;
        Onboarding.KeyButton.IsEnabled = PasteButton.IsEnabled;
        Onboarding.FileButton.IsEnabled = ImportFileButton.IsEnabled;
        if (profile is not null && Onboarding.Visibility == Visibility.Visible) Navigate("Connection");
        RefreshTrayPopup();
        RefreshMeasurement();
        FlushStateSave();
    }

    private void RecordProbe(ProbeResult result)
    {
        // Synchronize process identity first so the next refresh cannot erase this result.
        RefreshPresentation();
        lastProbe = result;
        lastCheck = DateTimeOffset.UtcNow;
        RefreshPresentation();
    }
}
