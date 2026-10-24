using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Oblachka;

// Добавление по QR-коду: сканирование экрана (код открыт в Telegram или браузере рядом) и
// картинка из буфера обмена (Ctrl+V в поле ключа). Распознанное идёт тем же путём, что и ввод.
public partial class MainWindow
{
    private void WireQr()
    {
        Onboarding.ScanButton.Click += async (_, _) => await ScanAndAddAsync();
        Servers.Add.ScanButton.Click += async (_, _) => { Servers.AddPopup.IsOpen = false; await ScanAndAddAsync(); };
        CommandManager.AddPreviewExecutedHandler(Onboarding.KeyInput, PasteImage);
    }

    private async Task ScanAndAddAsync()
    {
        if (operation is not null) return;
        // Наше окно может закрывать код: на время снимка оно уходит с экрана.
        Hide();
        string? text;
        try
        {
            await Task.Delay(300);
            text = await Task.Run(() =>
            {
                try { return QrImages.ScanScreens(); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { return null; }
            });
        }
        finally { Show(); Activate(); }
        if (text is null) { DetailText.Text = "QR-код на экране не найден. Откройте его крупнее, чтобы он был виден целиком, и нажмите ещё раз."; return; }
        await AddScannedAsync(text);
    }

    private async void PasteImage(object sender, ExecutedRoutedEventArgs args)
    {
        if (args.Command != ApplicationCommands.Paste || Clipboard.ContainsText() || !Clipboard.ContainsImage()) return;
        args.Handled = true;
        var image = Clipboard.GetImage();
        if (image is null) return;
        var text = await Task.Run(() => DecodeImage(image));
        if (text is null) { DetailText.Text = "На картинке не нашлось QR-кода."; return; }
        await AddScannedAsync(text);
    }

    private static string? DecodeImage(BitmapSource image)
    {
        try
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new MemoryStream(); encoder.Save(stream); stream.Position = 0;
            using var bitmap = new System.Drawing.Bitmap(stream);
            return QrImages.Decode(bitmap);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
    }

    // Первый запуск — в поле ключа и сразу «Подключиться». Дальше — как ссылка oblachka://:
    // подписка заменяет свой набор, отдельный профиль добавляется к сторонним.
    private async Task AddScannedAsync(string text)
    {
        text = text.Trim();
        if (DeepLink.IsLink(text) && DeepLink.Parse(text) is { } fromLink) text = fromLink;
        if (profile is null) { Onboarding.KeyInput.Text = text; await ContinueWithAsync(text, clearField: true); return; }
        try
        {
            switch (ServiceKey.Classify(text))
            {
                case ServiceKey.InputKind.Profiles: Import(text); break;
                case ServiceKey.InputKind.ExternalUrl: await LoadSubscriptionAsync(SubscriptionMirrors.ParseSources(text), 0); break;
                default: await LoadSubscriptionAsync(new[] { ServiceKey.ToSubscriptionUrl(text) }, 0); break;
            }
            Navigate("Connection");
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }
}
