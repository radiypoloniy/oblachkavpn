using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Oblachka;

// Картинки для поиска QR: снимки экранов и изображения в градации серого.
public static class QrImages
{
    public static (byte[] Luminance, int Width, int Height) Gray(Bitmap bitmap)
    {
        var (w, h) = (bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            var gray = new byte[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = y * data.Stride + x * 4;
                    // Прозрачное — как белый фон: QR в PNG часто без подложки.
                    var alpha = pixels[i + 3] / 255.0;
                    var value = (pixels[i + 2] * 299 + pixels[i + 1] * 587 + pixels[i] * 114) / 1000;
                    gray[y * w + x] = (byte)(value * alpha + 255 * (1 - alpha));
                }
            return (gray, w, h);
        }
        finally { bitmap.UnlockBits(data); }
    }

    public static string? Decode(Bitmap bitmap)
    {
        var (gray, w, h) = Gray(bitmap);
        return QrDecoder.Decode(gray, w, h);
    }

    // Все экраны по очереди: QR обычно в окне Telegram или браузера рядом с нашим.
    public static string? ScanScreens()
    {
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            using var shot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            if (Decode(shot) is { } text) return text;
        }
        return null;
    }
}
