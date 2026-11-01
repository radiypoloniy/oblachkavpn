namespace Oblachka;

public partial class MainWindow
{
    private void RecoveryStateChanged()
    {
        if (closed) return;
        if (runtime.Recovery.Message.Length > 0) DetailText.Text = runtime.Recovery.Message;
        UpdateControls();
    }
    private async void RecoveryCompleted(RecoveryOutcome result)
    {
        if (closed || manualStop || vmTestActive) return;
        routeAttempts = result.Attempts;
        if (result.Kind == RecoveryOutcomeKind.RequiresReconnect && result.Selected is { } next)
        {
            Notify(Notice.ServerSwitched, "Переподключаем VPN", "Пересоздаём сессию: на это время защита снимается.");
            DetailText.Text = "Переподключаемся на " + next.DisplayName + ": на это время защита снимается.";
            SelectProfile(next); profile = next;
            await StopSystemTunnelAsync(); UpdateControls();
            if (!closed && !manualStop && ConnectButton.IsEnabled) await ConnectCoreAsync();
            return;
        }
        if (result.Kind is RecoveryOutcomeKind.Error or RecoveryOutcomeKind.Exhausted)
        { DetailText.Text = result.Message ?? "Восстановить подключение не удалось."; RefreshHome(); return; }
        if (result.Kind == RecoveryOutcomeKind.Restarted)
        { Connected(); DetailText.Text = "Подключение запущено заново. Проверяем связь."; UpdateControls(); return; }
        if (result.Kind == RecoveryOutcomeKind.Recovered && result.Selected is { } selected)
        {
            if (!ReferenceEquals(result.Previous, selected))
                Notify(Notice.ServerSwitched, "Сервер сменился", result.Previous?.DisplayName + " перестал отвечать — теперь " + selected.DisplayName + ".");
            Servers.SearchBox.Clear(); ProfilePicker.ItemsSource = importedProfiles; ProfilePicker.SelectedItem = selected;
            if (runtime.Monitor.LastProbe is { } probe) RecordProbe(probe);
            PingsChanged(); LearnFragment(result.Fragment); QueueStateSave();
            DetailText.Text = result.Fragment ? "Соединение восстановлено с обходом DPI: рукопожатие разрезано." : "Соединение восстановлено; выбран рабочий сервер.";
            return;
        }
        networkClosed = SystemTunnelActive;
        Notify(Notice.NetworkClosed, SystemTunnelActive ? "Интернет закрыт" : "VPN не работает", "Рабочий сервер не найден. Нажмите, чтобы выбрать другой или выключить VPN.", warning: true);
        DetailText.Text = SystemTunnelActive ? "Рабочий сервер не найден. Блокировка остаётся: выберите сервер и подключитесь или нажмите «Остановить»."
            : "Рабочий сервер не найден. Автоподбор завершён; подключитесь вручную.";
    }
}
