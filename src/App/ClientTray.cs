using Forms = System.Windows.Forms;

namespace Oblachka;

internal enum TrayCommand { Open, Connect, Stop, Exit, Popup, Notice, Update }
internal interface IClientTrayView
{
    bool Closed { get; }
    void Handle(TrayCommand command);
}

// Значок принадлежит приложению. Слабая ссылка не удерживает закрытое представление.
internal sealed class ClientTray : IDisposable
{
    private readonly ClientRuntime owner;
    private readonly SynchronizationContext context;
    private readonly System.Drawing.Icon icon;
    private WeakReference<IClientTrayView>? view;
    private bool disposed, updateNotice;
    public Forms.NotifyIcon Icon { get; }
    public Forms.ToolStripMenuItem Connect { get; } = new("Подключить");
    public Forms.ToolStripMenuItem Stop { get; } = new("Остановить");
    public event Action? ViewRequested;

    public ClientTray(ClientRuntime owner, SynchronizationContext context)
    {
        this.owner = owner; this.context = context;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Request(TrayCommand.Open));
        menu.Items.Add(Connect); menu.Items.Add(Stop); menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Завершить", null, (_, _) => Request(TrayCommand.Exit));
        Connect.Click += (_, _) => Request(TrayCommand.Connect);
        Stop.Click += (_, _) => Request(TrayCommand.Stop);
        icon = AppIcon.Small();
        Icon = new Forms.NotifyIcon { Icon = icon, Text = ServiceBrand.Name + " — отключено", ContextMenuStrip = menu };
        Icon.DoubleClick += (_, _) => Request(TrayCommand.Open);
        Icon.BalloonTipClicked += (_, _) => { var command = updateNotice ? TrayCommand.Update : TrayCommand.Notice; updateNotice = false; Request(command); };
        Icon.MouseUp += (_, args) => { if (args.Button == Forms.MouseButtons.Left && args.Clicks < 2) Request(TrayCommand.Popup); };
        owner.Engine.Changed += EngineChanged;
        owner.Monitor.StateChanged += Refresh;
        owner.Recovery.StateChanged += Refresh;
        owner.Connections.Changed += Refresh;
        owner.Updates.AvailableInBackground += UpdateAvailable;
        Refresh();
    }
    public void Attach(IClientTrayView current) { view = new(current); updateNotice = false; }
    public void Detach(IClientTrayView current)
    { if (view?.TryGetTarget(out var target) == true && ReferenceEquals(target, current)) view = null; }
    private IClientTrayView? CurrentView() => view?.TryGetTarget(out var target) == true && !target.Closed ? target : null;
    internal void Request(TrayCommand command) => context.Post(_ =>
    {
        if (disposed) return;
        var target = CurrentView();
        if (target is null)
        {
            ViewRequested?.Invoke();
            target = CurrentView();
        }
        target?.Handle(command);
    }, null);
    private void EngineChanged(ConnectionState state, string message) => context.Post(_ => Refresh(), null);
    private void UpdateAvailable(AppRelease release)
    {
        if (disposed || CurrentView() is not null || !owner.Preferences.Notices) return;
        updateNotice = true;
        Icon.ShowBalloonTip(8000, "Вышла версия " + release.Tag.TrimStart('v'), "Нажмите, чтобы обновить.", Forms.ToolTipIcon.Info);
    }
    private void Refresh()
    {
        if (disposed) return;
        var busy = owner.Activity.Operation is not null || owner.Connections.IsActive || owner.Connections.IsStopping;
        var running = owner.Engine.State is ConnectionState.Running or ConnectionState.Verified;
        var tunnel = owner.Tunnel is { State: not (SystemTunnelState.Idle or SystemTunnelState.Failed) };
        Connect.Enabled = !busy && !running && !tunnel && owner.Profiles.Selected is not null;
        Stop.Enabled = busy || running || tunnel || owner.Engine.State == ConnectionState.Failed;
        Icon.Text = ServiceBrand.Name + " — " + (owner.Connections.IsStopping ? "Отключаемся" : running ? tunnel ? "VPN включён" : "Прокси запущен" : busy ? "Подключаемся" : "Отключено");
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        owner.Engine.Changed -= EngineChanged; owner.Monitor.StateChanged -= Refresh; owner.Recovery.StateChanged -= Refresh;
        owner.Connections.Changed -= Refresh;
        owner.Updates.AvailableInBackground -= UpdateAvailable;
        view = null; ViewRequested = null;
        Icon.Visible = false; Icon.ContextMenuStrip?.Dispose(); Icon.Dispose(); icon.Dispose();
    }
}
