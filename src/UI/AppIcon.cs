using System.Windows;

namespace Oblachka;

// Иконка приложения (Assets/app.ico): exe берёт её из ApplicationIcon, окна — из ресурса,
// трей — маленький кадр из того же файла.
public static class AppIcon
{
    public static System.Drawing.Icon Small()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
            if (stream is not null) using (stream) return new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { }
        return System.Drawing.SystemIcons.Application;
    }
}
