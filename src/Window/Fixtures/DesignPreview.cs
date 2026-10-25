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
        // «app@420» — та же страница в окне шириной 420: узкие раскладки.
        double? width = null;
        if (page.LastIndexOf('@') is var at and > 0 && double.TryParse(page[(at + 1)..], out var w)) { width = w; page = page[..at]; }
        if (page.EndsWith("-dark", StringComparison.Ordinal)) { ThemeManager.Apply(ThemeChoice.Dark); page = page[..^5]; }
        else ThemeManager.Apply(ThemeChoice.Light);
        Window? popup = null;
        try
        {
            if (compact) { main.Width = 960; main.Height = 660; }
            main.PrepareDesignPreview(page);
            if (width is { } narrow) main.NarrowPreview(narrow);
            Window target = main;
            if (page == "tray") { popup = main.CreateTrayPreview(); popup.Height = 420; target = popup; }
            if (page == "add")
            {
                popup = new Window { Width = 384, Height = 380, Content = new Views.AddMenu() };
                popup.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Page");
                target = popup;
            }
            if (page == "dialog")
            {
                var field = new System.Windows.Controls.TextBox { Text = "https://sub.example.com/s/abc" + Environment.NewLine + "https://mirror.example.net/s/abc", Height = 90, Margin = new Thickness(0, 14, 0, 0) };
                field.SetResourceReference(FrameworkElement.StyleProperty, "FieldBox");
                popup = AppDialog.Build(null, "Добавить подписку", "Ссылка подписки любого сервиса — или отдельные подключения vless://, trojan://, hy2://, JSON.", field, "Добавить", "Отмена", false);
                popup.Height = 360; popup.Show(); target = popup;
            }
            if (page == "confirm")
            {
                popup = AppDialog.Build(null, "Убрать «Основная»?", "Уберём и её 24 сервера. Вернуть можно, добавив подписку снова.", null, "Убрать", "Отмена", true);
                popup.Height = 240; popup.Show(); target = popup;
            }
            // Сообщения о действиях подготовки превью на снимке не нужны.
            main.Toast.Visibility = Visibility.Collapsed;
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
        // Фон окна рисует его шаблон, а снимается только содержимое — подкладываем фон сами.
        var back = new DrawingVisual();
        using (var context = back.RenderOpen()) context.DrawRectangle(target.Background, null, new Rect(0, 0, target.Width, target.Height));
        bitmap.Render(back);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path); encoder.Save(output);
    }
}
