using System.Windows;

namespace Oblachka;

// Точка входа и повторный запуск хранят владельца приложения, а не замыкание первого окна.
internal sealed class ClientDesktop : IDisposable
{
    private readonly Application app;
    private readonly ClientRuntime runtime;
    private WeakReference<MainWindow>? view;
    private bool disposed;
    public ClientDesktop(Application app, ClientRuntime runtime) { this.app = app; this.runtime = runtime; }
    public MainWindow Window()
    {
        if (view?.TryGetTarget(out var existing) == true && !existing.IsClosed) return existing;
        var window = new MainWindow(runtime);
        view = new(window);
        runtime.Tray!.ViewRequested -= ViewRequested;
        runtime.Tray.ViewRequested += ViewRequested;
        return window;
    }
    private void ViewRequested() { if (!disposed && !app.Dispatcher.HasShutdownStarted) Window().Show(); }
    public void Start(bool hidden, string? link)
    {
        var window = Window();
        if (hidden) window.StartHiddenInTray();
        if (link is not null) window.QueueDeepLink(link);
        window.Show();
    }
    public void ShowFromSignal() => app.Dispatcher.BeginInvoke(() =>
    { if (!disposed && !app.Dispatcher.HasShutdownStarted) Window().ShowFromSignal(); });
    public void Close()
    { if (view?.TryGetTarget(out var current) == true && !current.IsClosed) current.Close(); }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (runtime.Tray is not null) runtime.Tray.ViewRequested -= ViewRequested;
        Close(); view = null;
    }
}
