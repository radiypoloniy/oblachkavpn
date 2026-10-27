using System.Windows;
using System.Windows.Threading;

namespace Oblachka;

// «Помощь → Обновления»: проверка релизов GitHub (AppUpdate) сама раз в 12 часов и по кнопке,
// установка по нажатию. О найденной версии сообщаем один раз за запуск.
public partial class MainWindow
{
    private AppRelease? availableUpdate;
    private bool updateBusy, updateAnnounced;
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    private void WireUpdates()
    {
        AppUpdate.CleanUp();
        AppTab.UpdateButton.Click += async (_, _) =>
        {
            // Ждёт подписи — кнопка проверяет снова: подпись прикладывают вскоре после выпуска.
            if (availableUpdate is not { Signature: not null }) await CheckUpdateAsync(manual: true);
            else await InstallUpdateAsync();
        };
        AppTab.UpdateAuto.Click += (_, _) => QueueStateSave();
        // Сменился канал — найденная раньше версия могла быть не из него.
        AppTab.UpdatePrerelease.Click += (_, _) => { availableUpdate = null; updateChecked = false; QueueStateSave(); RefreshUpdate(); };
        // Первая проверка — через полминуты после запуска, чтобы не мешать подключению.
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = TimeSpan.FromHours(12);
            if (AppTab.UpdateAuto.IsChecked == true && stateStore is not null && !isolatedPersistence) await CheckUpdateAsync(manual: false);
        };
        updateTimer.Start();
        RefreshUpdate();
    }

    private async Task CheckUpdateAsync(bool manual)
    {
        if (updateBusy || closed) return;
        updateBusy = true;
        if (manual) AppTab.UpdateText.Text = "Проверяем…";
        RefreshUpdate();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var release = await AppUpdate.CheckAsync(AppTab.UpdatePrerelease.IsChecked == true, timeout.Token);
            availableUpdate = release is not null && AppUpdate.IsNewer(release) ? release : null;
            if (availableUpdate is { Signature: not null } && !manual && !updateAnnounced)
            {
                updateAnnounced = true;
                DetailText.Text = $"Доступна {ReleaseName(availableUpdate)}. Обновить: «Приложение → Обновления».";
                Notify(Notice.Update, "Вышла " + ReleaseName(availableUpdate), "Нажмите, чтобы обновить.", () => { ShowWindow(); OpenSettings(Views.SettingsPanel.AppPage); });
            }
            updateError = null; updateChecked = true;
        }
        catch (Exception ex) when (ex is UserError or HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException)
        {
            // Без сети автоматическая проверка молчит; по кнопке — объясняем.
            updateError = ex is UserError ? ex.Message : "Не удалось связаться с GitHub. Проверьте интернет и попробуйте ещё раз.";
            if (!manual) updateError = null;
        }
        finally { updateBusy = false; if (!closed) RefreshUpdate(); }
    }

    private string? updateError;
    private bool updateChecked;

    private void RefreshUpdate()
    {
        var help = AppTab;
        help.UpdateButton.IsEnabled = !updateBusy;
        if (updateBusy) return;
        if (availableUpdate is { } release)
        {
            help.UpdateTitle.Text = (release.Signature is null ? "Вышла " : "Доступна ") + ReleaseName(release);
            help.UpdateText.Text = release.Signature is null ? "Ждёт подписи разработчика — обновить можно будет чуть позже. Ничего делать не нужно."
                : $"Сейчас {ServiceBrand.Version}. Установщик {release.Size / (1024 * 1024)} МБ, проверяется по SHA256 и подписи";
            help.UpdateButton.Content = release.Signature is null ? "Проверить" : "Обновить";
        }
        else
        {
            help.UpdateTitle.Text = "Обновления";
            help.UpdateText.Text = updateError ?? "Версия " + ServiceBrand.Version + (updateChecked ? " — последняя" : "");
            help.UpdateButton.Content = "Проверить";
        }
    }

    // «версия 0.14.2» или «пробная версия 0.14.2-rc.3».
    private static string ReleaseName(AppRelease release) => release.Prerelease ? "пробная версия " + release.Tag.TrimStart('v') : "версия " + release.Version;

    private async Task InstallUpdateAsync()
    {
        if (availableUpdate is not { } release || updateBusy) return;
        var connected = engine.State is ConnectionState.Running or ConnectionState.Verified;
        if (!AppDialog.Confirm(this, "Обновить до " + (release.Prerelease ? release.Tag.TrimStart('v') : release.Version.ToString()) + "?",
            (release.Prerelease ? "Это пробная версия: она может работать хуже обычной. " : "") +
            "Скачаем установщик с GitHub и сверим контрольную сумму. Windows попросит права администратора. " +
            (connected ? "На время установки подключение прервётся; " : "") + "после установки Облачка откроется сама. Подписки и настройки сохранятся.",
            "Обновить")) return;
        updateBusy = true; RefreshUpdate();
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var progress = new Progress<double>(share => AppTab.UpdateText.Text = $"Скачиваем… {share:P0}");
            var installer = await AppUpdate.DownloadAsync(release, progress, cancel.Token);
            AppTab.UpdateText.Text = "Запускаем установщик…";
            AppUpdate.Launch(installer);
            // Установщик сам закроет окно; выходим раньше, чтобы штатно вернуть сеть.
            ExitApplication();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            updateBusy = false; RefreshUpdate();
            AppTab.UpdateText.Text = "Установка отменена: Windows не получила разрешения.";
        }
        catch (Exception ex) when (ex is UserError or HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            updateBusy = false; RefreshUpdate();
            AppTab.UpdateText.Text = ex is UserError ? ex.Message : "Скачать обновление не удалось. Проверьте интернет и попробуйте ещё раз.";
        }
    }
}
