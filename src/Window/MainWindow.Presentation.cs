using System.Windows;
using System.Windows.Threading;

namespace Oblachka;

public partial class MainWindow
{
    private static readonly TimeSpan VisiblePresentationInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HiddenPresentationInterval = TimeSpan.FromMinutes(1);
    private readonly DispatcherTimer presentationTimer = new();
    private DateTimeOffset? sessionStarted;
    private DateTimeOffset? lastCheck { get => runtime.Monitor.LastCheck; set => runtime.Monitor.LastCheck = value; }
    private ProbeResult? lastProbe { get => runtime.Monitor.LastProbe; set => runtime.Monitor.LastProbe = value; }
    private int? presentedPid;
    private bool findingRoute { get => runtime.Activity.FindingRoute; set => runtime.Activity.FindingRoute = value; }

    private bool PresentationVisible => (IsVisible && WindowState != WindowState.Minimized) || trayPopup?.IsVisible == true;

    private void WirePresentation()
    {
        IsVisibleChanged += (_, _) => PresentationVisibilityChanged();
        StateChanged += (_, _) => PresentationVisibilityChanged();
        presentationTimer.Tick += (_, _) =>
        {
            if (PresentationVisible) RefreshPresentation();
            PollZapretRouting();
            PollZapretWatch();
            PollExpiry();
        };
        UpdatePresentationInterval();
        presentationTimer.Start();
    }

    private void PresentationVisibilityChanged()
    {
        if (closed) return;
        UpdatePresentationInterval();
        if (PresentationVisible) RefreshPresentation();
    }

    private void UpdatePresentationInterval()
    {
        // Пока ещё действует старый маршрут zapret, сохраняем частоту повторных попыток
        // даже после выключения обхода. Уведомлению о сроке подписки достаточно минуты.
        var interval = PresentationVisible || zapretEnabled || coreZapretKey.Length > 0
            ? VisiblePresentationInterval : HiddenPresentationInterval;
        if (presentationTimer.Interval != interval) presentationTimer.Interval = interval;
    }

    private void RefreshPresentation()
    {
        if (!viewsReady) return;
        var state = engine.State;
        var pid = engine.ProcessId;
        runtime.Monitor.ObserveProcess();
        if (pid != presentedPid)
        {
            presentedPid = pid;
            sessionStarted = pid is null ? null : DateTimeOffset.UtcNow;
            // Новое ядро — сразу перепроверяем, иначе чип «Проверяем связь…» ждал бы плановые 45 с.
        }
        RefreshTraffic();
        if (sheetMeasurementsDirty && Servers.IsVisible) FilterProfiles();
        RefreshHome();
        RefreshRuleApplyStatus();
        Servers.SearchBox.IsEnabled = ProfilePicker.IsEnabled;
        if (Servers.IsVisible) RefreshServerListControls();
        Diagnostics.CoreState.Text = pid is null ? "Не запущен" : "Готов";
        Diagnostics.NetworkNote.Text = SystemTunnelActive
            ? "Включён системный режим. Запрос ниже проверяет связь через локальное ядро; проверка DNS, IPv6 и блокировки обхода выполняется отдельно."
            : "Запрос ниже проверяет связь через локальный прокси. Он не проверяет DNS, IPv6 и трафик остальных программ.";
        Diagnostics.TunnelState.Text = lastProbe is null ? "Не проверен" : lastProbe.Success ? $"{lastProbe.Milliseconds} мс" : "Не прошёл";
        Diagnostics.Heading.Text = lastProbe is { Success: true } ? "Туннель проверен" : "Проверим подключение";
        Diagnostics.LastCheck.Text = lastCheck is null ? "Проверка ещё не выполнялась" : "Последняя проверка " + lastCheck.Value.ToLocalTime().ToString("HH:mm:ss");
        Onboarding.ContinueButton.IsEnabled = operation is null;
        Onboarding.KeyInput.IsEnabled = operation is null;
        Onboarding.FileButton.IsEnabled = ImportFileButton.IsEnabled;
        Onboarding.ScanButton.IsEnabled = Servers.Add.ScanButton.IsEnabled = operation is null;
        Onboarding.ForeignList.IsEnabled = operation is null;
        if (profile is not null && Onboarding.Visibility == Visibility.Visible) Navigate("Connection");
        else if (Onboarding.Visibility == Visibility.Visible) FindForeignClients();
        RefreshTrayPopup();
        if (Servers.IsVisible) RefreshMeasurement();
    }

    private void RecordProbe(ProbeResult result)
    {
        // Synchronize process identity first so the next refresh cannot erase this result.
        RefreshPresentation();
        lastProbe = result;
        lastCheck = DateTimeOffset.UtcNow;
        if (result.Success && runningProfile is { } working) RememberWorking(working);
        RefreshPresentation();
    }
}
