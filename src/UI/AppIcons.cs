using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Oblachka;

// Значки программ для выбора в правилах: из самого .exe, замороженные, чтобы их можно было
// получать в фоне и показывать в окне. Кэш хранит до 256 последних значков; видимые строки держат свои ссылки.
public static class AppIcons
{
    private static readonly BoundedCache<string, ImageSource?> cache = new(256, StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim slots = new(4);

    internal static async Task<ImageSource?> ForAsync(string path)
    {
        await slots.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => For(path)).ConfigureAwait(false); }
        finally { slots.Release(); }
    }

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
