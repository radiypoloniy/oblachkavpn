using System.Diagnostics;
using System.Windows;

namespace Oblachka;

// «Помощь → Сеть Windows»: проверка (NetworkDoctor), исправление найденного (NetworkRepair — в окне,
// NetworkRepairBackend — службой), «Вернуть как было» и встроенный «Сброс сети» Windows.
public partial class MainWindow
{
    private IReadOnlyList<NetworkFinding> findings = Array.Empty<NetworkFinding>();
    private bool doctorBusy;

    private void WireDoctor()
    {
        Diagnostics.NetworkCheckButton.Click += async (_, _) => { OpenDoctor(); await CheckNetworkAsync(); };
        Doctor.BackButton.Click += (_, _) => Doctor.Visibility = Visibility.Collapsed;
        Doctor.RecheckButton.Click += async (_, _) => await CheckNetworkAsync();
        Doctor.FixButton.Click += async (_, _) => await FixNetworkAsync();
        Doctor.UndoButton.Click += async (_, _) =>
        {
            Doctor.Summary.Text = NetworkRepair.Undo() ? "Прежние настройки прокси возвращены." : "Возвращать нечего.";
            await CheckNetworkAsync(keepSummary: true);
        };
        Doctor.WindowsResetButton.Click += (_, _) => OpenWindowsReset();
    }

    private void OpenDoctor()
    {
        Doctor.Visibility = Visibility.Visible;
        Doctor.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    private async Task CheckNetworkAsync(bool keepSummary = false)
    {
        if (doctorBusy) return;
        doctorBusy = true; RefreshDoctor();
        if (!keepSummary) Doctor.Summary.Text = "Проверяем…";
        try
        {
            findings = await Task.Run(() => NetworkDoctor.Diagnose(new WindowsNetworkProbe()));
            if (!keepSummary)
                Doctor.Summary.Text = findings.All(f => f.Action.Kind is RepairKind.None or RepairKind.FlushDns)
                    ? findings.Count == 0 ? "Поломок не нашли: прокси, DNS, маршруты и обходы блокировок в порядке." : "Чинить нечего, но есть что знать:"
                    : "Нашли то, что мешает интернету:";
        }
        finally { doctorBusy = false; RefreshDoctor(); }
    }

    private async Task FixNetworkAsync()
    {
        if (doctorBusy) return;
        var todo = findings.Where(f => f.Action.Kind != RepairKind.None).ToArray();
        if (todo.Length == 0) return;
        doctorBusy = true; RefreshDoctor();
        int fixedCount = 0; var failed = new List<string>();
        try
        {
            IHelperLifetime? temporary = null;
            try
            {
                foreach (var finding in todo.OrderBy(f => f.Action.Kind == RepairKind.FlushDns))
                {
                    try
                    {
                        if (!finding.Action.Elevated) { NetworkRepair.ApplyLocal(finding.Action); fixedCount++; continue; }
                        // Служба — та же, что держит «Весь компьютер»: при подключении — её канал, иначе свой.
                        var client = helperLifetime?.Client;
                        if (client is null)
                        {
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                            temporary ??= await NetworkHelperConnection.ConnectAsync(timeout.Token);
                            client = temporary.Client;
                        }
                        using var repairTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        if (await client.RepairAsync(new RepairRequest(finding.Action.Kind, finding.Action.Target), repairTimeout.Token)) fixedCount++;
                    }
                    catch (Exception ex) when (ex is UserError or IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    { failed.Add(finding.Title); }
                }
            }
            finally { if (temporary is not null) await temporary.DisposeAsync(); }
        }
        finally { doctorBusy = false; }
        Doctor.Summary.Text = (fixedCount > 0 ? "Исправлено: " + fixedCount + ". " : "") +
            (failed.Count > 0 ? "Не получилось: " + string.Join(", ", failed) + "." : "Если сайты всё ещё не открываются, перезапустите браузер.");
        await CheckNetworkAsync(keepSummary: true);
    }

    private void RefreshDoctor()
    {
        Doctor.FindingList.ItemsSource = findings;
        var fixable = findings.Any(f => f.Action.Kind is not (RepairKind.None or RepairKind.FlushDns));
        Doctor.FixButton.IsEnabled = !doctorBusy && fixable;
        Doctor.UndoButton.IsEnabled = !doctorBusy && NetworkRepair.HasBackup;
        Doctor.RecheckButton.IsEnabled = !doctorBusy;
    }

    // Последнее средство — встроенный «Сброс сети» Windows: переустанавливает адаптеры и
    // перезагружает компьютер. Сами мы winsock и стек TCP/IP не сбрасываем.
    private void OpenWindowsReset()
    {
        var answer = MessageBox.Show(this,
            "Откроем «Сброс сети» в параметрах Windows. Он удалит и заново установит все сетевые адаптеры, включая VPN, " +
            "сотрёт настройки Wi-Fi и вручную введённые IP и DNS и перезагрузит компьютер.\n\nСначала попробуйте «Исправить найденное». Открыть?",
            "Полный сброс сети", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        var page = Environment.OSVersion.Version.Build >= 22000 ? "ms-settings:network-advancedsettings" : "ms-settings:network-status";
        try { Process.Start(new ProcessStartInfo(page) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { DetailText.Text = "Не удалось открыть параметры Windows: Пуск → Параметры → Сеть → Сброс сети."; }
    }
}
