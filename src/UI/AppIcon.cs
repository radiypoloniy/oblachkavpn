namespace Oblachka;

// Иконка приложения (Assets/app.ico): exe берёт её из ApplicationIcon, окна — из ресурса,
// трей — маленький кадр из отдельного manifest resource того же файла, без загрузки WPF.
public static class AppIcon
{
    public static System.Drawing.Icon Small()
    {
        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Oblachka.TrayIcon");
            if (stream is not null)
            {
                using var source = new System.Drawing.Icon(stream, System.Windows.Forms.SystemInformation.SmallIconSize);
                return (System.Drawing.Icon)source.Clone();
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException) { }
        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }
}
