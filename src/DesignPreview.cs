using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RoutePilot;

public static class DesignPreview
{
    public static int Capture(string path, string page, bool compact)
    {
        FlagImages.AllowNetwork = false;
        var app = new Application();
        var main = new MainWindow(persistenceEnabled: false);
        Window? popup = null;
        try
        {
            if (compact) { main.Width = 960; main.Height = 660; }
            main.PrepareDesignPreview(page);
            Window target = main;
            if (page == "tray") { popup = main.CreateTrayPreview(); target = popup; }
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
