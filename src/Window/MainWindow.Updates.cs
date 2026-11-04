using System.Windows;

namespace Oblachka;

// «Помощь → Обновления»: проверка релизов GitHub (AppUpdate) сама раз в 12 часов и по кнопке,
// установка по нажатию. О найденной версии сообщаем один раз за запуск.
public partial class MainWindow
{
    private AppRelease? availableUpdate => runtime.Updates.Available;
    private bool updateBusy { get => runtime.Updates.Busy; set => runtime.Updates.Installing = value; }
    private string? updateError => runtime.Updates.Error;
    private bool updateChecked => runtime.Updates.Checked;
    private void WireUpdates()
    {
        runtime.Updates.Changed += UpdatesChanged;
        runtime.Updates.AvailableInBackground += BackgroundUpdateAvailable;
        AppTab.UpdateButton.Click += async (_, _) =>
        {
            if (availableUpdate is not { Signature: not null }) await CheckUpdateAsync(manual: true);
            else await InstallUpdateAsync();
        };
        AppTab.UpdateAuto.Click += (_, _) => QueueStateSave();
        AppTab.UpdatePrerelease.Click += (_, _) => { QueueStateSave(); RefreshUpdate(); };
        RefreshUpdate();
    }
    private async Task CheckUpdateAsync(bool manual)
    {
        if (closed || updateBusy) return;
        if (manual) AppTab.UpdateText.Text = "Проверяем…";
        await runtime.Updates.CheckAsync(manual);
    }
    private void UpdatesChanged() { if (!closed) RefreshUpdate(); }
    private void BackgroundUpdateAvailable(AppRelease release)
    {
        if (closed) return;
        DetailText.Text = $"Доступна {release.DisplayName}. Обновить: «Приложение → Обновления».";
    }

    private void RefreshUpdate()
    {
        var help = AppTab;
        help.UpdateButton.IsEnabled = !updateBusy;
        if (updateBusy) return;
        if (availableUpdate is { } release)
        {
            help.UpdateTitle.Text = (release.Signature is null ? "Вышла " : "Доступна ") + release.DisplayName;
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
