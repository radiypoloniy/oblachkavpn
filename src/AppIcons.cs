using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Oblachka;

// Значки программ для выбора в правилах: из самого .exe, замороженные, чтобы их можно было
// получать в фоне и показывать в окне. Кэш по пути живёт до конца запуска.
public static class AppIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string path) => cache.GetOrAdd(path, static p =>
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(p);
            if (icon is null) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException) { return null; }
    });
}
