using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Oblachka;
using Forms = System.Windows.Forms;

// Изолированный владелец подключения с настоящим треем и WinForms message loop, без WPF Application.
// Никаких реальных ядер, системного прокси, пользовательского состояния или внешних запросов.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var baseline = args.Length == 2 && args[0] == "--wpf-baseline";
        var views = args.Length == 3 && args[0] == "--views";
        if (args.Length != 1 && !baseline && !views) return 1;
        var report = Path.GetFullPath(args[^1]);
        var root = Path.Combine(Path.GetDirectoryName(report)!, "state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Контроль старого пути ресурсов значка. WPF создаётся только в этом явном режиме.
            if (baseline)
            {
                var app = Type.GetType("System.Windows.Application, PresentationFramework", throwOnError: true)!;
                Activator.CreateInstance(app);
            }
            using var loop = new Forms.ApplicationContext();
            using var context = new Forms.WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            using var owner = new ClientRuntime(new FixtureCore());
            var profile = Profiles.Parse("vless://11111111-1111-4111-8111-111111111111@example.invalid:443?security=tls&type=tcp#Background")
                with { Group = ProfileGroup.ManualId };
            owner.Profiles.Imported = new[] { profile }; owner.Profiles.Selected = owner.Profiles.Running = profile;
            owner.Profiles.Groups.Add(ProfileGroup.Manual); owner.Profiles.GroupProfiles[ProfileGroup.ManualId] = owner.Profiles.Imported;
            owner.Persistence.Configure(new ClientStateStore(Path.Combine(root, "state.bin")), isolated: true);
            owner.Persistence.Start(context);
            owner.Monitor.Start(context);
            var proxyStore = new FixtureProxy(); owner.Preferences.SystemProxy = true;
            owner.Proxy.Start(context, new SystemProxy(proxyStore, Path.Combine(root, "proxy.json")), false);
            // Доступ к internal только в диагностике; у рабочего клиента API не расширяется.
            var tray = typeof(ClientRuntime).GetMethod("EnsureTray", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, new object[] { context })!;
            var icon = (Forms.NotifyIcon)tray.GetType().GetProperty("Icon")!.GetValue(tray)!;
            icon.Visible = true;
            if (views)
            {
                var result = 2;
                context.Post(async _ =>
                {
                    try { await IpcProbe.RunAsync(owner, context, Path.GetFullPath(args[1]), report); result = 0; }
                    catch (Exception error) { File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = false, Error = error.ToString() })); }
                    finally { loop.ExitThread(); }
                }, null);
                Forms.Application.Run(loop); return result;
            }
            var changed = false; var done = false; var exitCode = 2;
            using var timer = new Forms.Timer { Interval = 1000 };
            var started = DateTime.UtcNow;
            timer.Tick += (_, _) =>
            {
                var elapsed = DateTime.UtcNow - started;
                if (!changed && elapsed.TotalSeconds >= 5)
                {
                    changed = true;
                    owner.Workspace.Favorites.Add(AutoPool.Key(profile)); owner.Persistence.Queue();
                    proxyStore.Current = new(0, null, null, null);
                }
                // Старый стражник в контрольной сборке иногда пропускает ранний тик (исправлено в клиенте).
                // Одинаковые 18 с в обоих вариантах оставляют время для проверки и старого пути.
                if (done || elapsed.TotalSeconds < 18) return;
                done = true; timer.Stop();
                try
                {
                    var health = owner.Monitor.LastObservation?.Round.Verified == true;
                    var saved = owner.Persistence.Store!.Load()?.Favorites.Length == 1;
                    var proxy = proxyStore.Current.ProxyServer == "127.0.0.1:10808";
                    var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).OrderBy(n => n).ToArray();
                    var wpf = assemblies.Where(n => n is "PresentationFramework" or "PresentationCore" or "WindowsBase").ToArray();
                    var noWpf = !wpf.Contains("PresentationFramework") && !wpf.Contains("PresentationCore");
                    var resource = typeof(AppIcon).Assembly.GetManifestResourceInfo("Oblachka.TrayIcon") is not null;
                    var iconSize = icon.Icon?.Size == Forms.SystemInformation.SmallIconSize;
                    exitCode = health && saved && proxy && icon.Visible && iconSize && (baseline || noWpf && resource) ? 0 : 2;
                    File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = exitCode == 0, ForcedGc = false, WpfBaseline = baseline,
                        Network = "Synthetic core and proxy store; encrypted state isolated beside report; no real VPN or external requests",
                        HealthVerified = health, StateSaved = saved, ProxyRepaired = proxy, TrayVisible = icon.Visible,
                        TrayResourcePresent = resource, TrayIconSizeCorrect = iconSize,
                        WpfAssemblies = wpf, Assemblies = assemblies, Memory = ProcessMemory.Read() }, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception error) { File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = false, Error = error.ToString() })); }
                finally { loop.ExitThread(); }
            };
            timer.Start(); Forms.Application.Run(loop);
            return exitCode;
        }
        catch (Exception error)
        { File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = false, Error = error.ToString() })); return 2; }
    }
    private sealed class FixtureCore : ITunnelController
    {
        private readonly int ownerThread = Environment.CurrentManagedThreadId;
        private void CheckThread()
        { if (Environment.CurrentManagedThreadId != ownerThread) throw new InvalidOperationException("Command left the owner context"); }
        public int Port => 10808;
        public int? ProcessId => State == ConnectionState.Running ? 42 : null;
        public ConnectionState State { get; private set; } = ConnectionState.Running;
        public string CoreLabel => "background-fixture";
        public event Action<ConnectionState, string>? Changed { add { } remove { } }
        public Task StartAsync(Profile profile, CancellationToken token) { CheckThread(); State = ConnectionState.Running; return Task.CompletedTask; }
        public Task<ProbeResult> ProbeAsync(Uri target, CancellationToken token) { CheckThread(); return Task.FromResult(new ProbeResult(true, 1, "fixture")); }
        public void Stop() { CheckThread(); State = ConnectionState.Idle; }
        public void Dispose() { }
    }
    private sealed class FixtureProxy : IProxySettingsStore
    {
        public ProxySettings Current = new(0, null, null, null);
        public ProxySettings Read() => Current;
        public void Write(ProxySettings settings) => Current = settings;
    }
}
