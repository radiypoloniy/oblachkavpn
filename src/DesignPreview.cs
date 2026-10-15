using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Oblachka;

public static class DesignPreview
{
    public static int Capture(string path, string page, bool compact)
    {
        FlagImages.AllowNetwork = false;
        var app = new Application();
        var main = new MainWindow(persistenceEnabled: false);
        if (page.EndsWith("-dark", StringComparison.Ordinal)) { ThemeManager.Apply(ThemeChoice.Dark); page = page[..^5]; }
        else ThemeManager.Apply(ThemeChoice.Light);
        Window? popup = null;
        try
        {
            if (compact) { main.Width = 960; main.Height = 660; }
            main.PrepareDesignPreview(page);
            Window target = main;
            if (page == "tray") { popup = main.CreateTrayPreview(); target = popup; }
            if (page == "dialog")
            {
                var field = new System.Windows.Controls.TextBox { Text = "https://sub.example.com/s/abc" + Environment.NewLine + "https://mirror.example.net/s/abc", Height = 90, Margin = new Thickness(0, 14, 0, 0) };
                field.SetResourceReference(FrameworkElement.StyleProperty, "FieldBox");
                popup = AppDialog.Build(null, "Добавить подписку", "Ссылка подписки любого сервиса — или отдельные подключения vless://, trojan://, hy2://, JSON.", field, "Добавить", "Отмена", false);
                popup.Height = 360; popup.Show(); target = popup;
            }
            if (page == "confirm")
            {
                popup = AppDialog.Build(null, "Убрать «Liberty»?", "Уберём и её 24 сервера. Вернуть можно, добавив подписку снова.", null, "Убрать", "Отмена", true);
                popup.Height = 240; popup.Show(); target = popup;
            }
            Render(target, path);
            return 0;
        }
        finally { popup?.Close(); main.Close(); }
    }

    internal static void Render(Window target, string path)
    {
        var surface = (FrameworkElement)target.Content;
        surface.Measure(new Size(target.Width, target.Height));
        surface.Arrange(new Rect(0, 0, target.Width, target.Height));
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)target.Width, (int)target.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
