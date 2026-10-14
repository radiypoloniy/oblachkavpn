using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

public enum ConnectionState { Idle, Validating, Starting, Running, Verified, Failed }
public enum ProbeFailure { None, Reachability, TargetResponse }
public sealed record ProbeResult(bool Success, long Milliseconds, string Message, ProbeFailure Failure = ProbeFailure.None);

public sealed class Engine : ITunnelController
{
    private CoreAdapter adapter;
    private Process? process;
    private ProcessJob? job;
    private bool stopping;
    private int preferredPort;
    public int Port { get; private set; }
    public int? ProcessId => process is { HasExited: false } ? process.Id : null;
    public ConnectionState State { get; private set; }
    public event Action<ConnectionState, string>? Changed;
#if TEST_HOOKS
    // Used only by local synthetic fixtures. Absent from the shipped build.
    internal Action<string>? FixtureTrace;
#endif

    public string CoreLabel => adapter.Label;
    public Engine(string executable, string runtimeRoot) { adapter = new(CoreKind.Xray, executable); }
    internal Engine(CoreAdapter adapter) { this.adapter = adapter; }
    private void Set(ConnectionState state, string message) { State = state; Changed?.Invoke(state, message); }

    public Task StartAsync(JsonObject outbound, CancellationToken token) => StartCoreAsync(port => adapter.RuntimeConfig(outbound, port), token);

    // Правила сайтов для обычного режима: их исполняет само ядро подключения. В системном
    // режиме окно оставляет список пустым — правила там у ядра туннеля.
    public IReadOnlyList<SiteRule> SiteRules { get; set; } = Array.Empty<SiteRule>();
    // Списки сервиса и встроенные наборы: исполняет ядро подключения в обоих режимах. В системном
    // режиме его прямой трафик возвращается в туннель и выпускается правилом CoreDirect службы.
    public CompiledRouting? Routing { get; set; }

    public Task StartAsync(Profile profile, CancellationToken token)
    {
        if (process is not null) throw new UserError("Сначала остановите текущее подключение.");
        var filename = profile.Core == CoreKind.Xray ? "xray.exe" : "sing-box.exe";
        adapter = new(profile.Core, Path.Combine(Path.GetDirectoryName(adapter.Executable)!, filename));
        var rules = SiteRules; var routing = Routing;
        return rules.Count == 0 && routing is null ? StartAsync(profile.Outbound, token)
            : StartCoreAsync(port => RoutingPlan.Compile(adapter, profile, port, rules, routing), token);
    }

    internal async Task StartCoreAsync(Func<int, JsonObject> configFactory, CancellationToken token)
    {
        if (process is not null) throw new UserError("Сначала остановите текущее подключение.");
        if (!File.Exists(adapter.Executable)) throw new UserError("Не найден файл ядра " + adapter.Label + ". Восстановите комплект приложения.");
        stopping = false;
        try
        {
            Set(ConnectionState.Validating, "Проверяем конфигурацию");
            Port = FreePort(preferredPort);
            preferredPort = Port;
            var config = configFactory(Port).ToJsonString();
            using (var validator = new Process { StartInfo = StartInfo(adapter.ValidationArguments) })
            {
                validator.Start();
                using var validationJob = new ProcessJob();
                try { validationJob.Attach(validator); }
                catch { TryKill(validator); throw; }
                var stdout = validator.StandardOutput.ReadToEndAsync();
                var stderr = validator.StandardError.ReadToEndAsync();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await validator.StandardInput.WriteAsync(config.AsMemory(), timeout.Token);
                    validator.StandardInput.Close();
                    await validator.WaitForExitAsync(timeout.Token);
                }
                catch { TryKill(validator); throw; }
                await Task.WhenAll(stdout, stderr); // Raw core output is never persisted or shown: it can contain credentials.
                if (validator.ExitCode != 0) throw new UserError(adapter.Label + " отклонил конфигурацию. Проверьте профиль и совместимость настроек.");
            }
            token.ThrowIfCancellationRequested();
            Set(ConnectionState.Starting, "Запускаем " + adapter.Label);
            job = new ProcessJob();
            process = new Process { StartInfo = StartInfo(adapter.RunArguments), EnableRaisingEvents = true };
            process.Exited += OnExited;
            process.Start();
            try { job.Attach(process); } catch { TryKill(process); throw; }
#if TEST_HOOKS
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) FixtureTrace?.Invoke(args.Data); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) FixtureTrace?.Invoke(args.Data); };
#endif
            // Потоки ядра всё равно вычитываются: без этого процесс может заблокироваться на записи.
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.StandardInput.WriteAsync(config.AsMemory(), token);
            process.StandardInput.Close();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(8));
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new UserError("Ядро завершилось при запуске. Проверьте профиль или попробуйте другой локальный порт.");
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, Port, startup.Token);
                    // SOCKS negotiation distinguishes the engine from an unrelated listener in the port allocation race.
                    await client.GetStream().WriteAsync(new byte[] { 5, 1, 0 }, startup.Token);
                    var response = new byte[2];
                    await client.GetStream().ReadExactlyAsync(response, startup.Token);
                    if (response[0] == 5 && response[1] == 0) break;
                }
                catch (SocketException) { }
                catch (IOException) { }
                await Task.Delay(100, startup.Token);
            }
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new UserError("Ядро завершилось при запуске.");
            Set(ConnectionState.Running, "Локальный прокси готов. Туннель ещё не проверен.");
        }
        catch (OperationCanceledException) { Stop(); throw; }
        catch (UserError) { Stop(); Set(ConnectionState.Failed, "Подключение не запущено"); throw; }
        catch (Exception ex) { Stop(); Set(ConnectionState.Failed, "Подключение не запущено"); throw new UserError("Не удалось запустить ядро. Проверьте доступ к папке приложения и наличие " + adapter.Label + ".", ex); }
    }

    public async Task<ProbeResult> ProbeAsync(Uri target, CancellationToken token)
    {
        if (process is null || process.HasExited) throw new UserError("Сначала запустите подключение.");
        var pid = process.Id;
        var port = Port;
        var watch = Stopwatch.StartNew();
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"socks5://127.0.0.1:{port}", false), UseProxy = true,
            AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8),
            UseCookies = false
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (process is null || process.HasExited || process.Id != pid) throw new UserError("Подключение остановлено во время проверки.");
            if (!response.IsSuccessStatusCode)
            {
                Set(ConnectionState.Running, "Сайт проверки вернул ошибку; это не доказывает отказ всего туннеля.");
                return new(false, watch.ElapsedMilliseconds, $"Ответ сайта: HTTP {(int)response.StatusCode}. Попробуйте другой адрес проверки.", ProbeFailure.TargetResponse);
            }
            Set(ConnectionState.Verified, "Запрос через туннель выполнен");
            return new(true, watch.ElapsedMilliseconds, $"Запрос через SOCKS выполнен за {watch.ElapsedMilliseconds} мс. Это время запроса, а не ping.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (process is { HasExited: false } && process.Id == pid) Set(ConnectionState.Running, "Ядро работает, но запрос через туннель не выполнен.");
            return new(false, watch.ElapsedMilliseconds, "Запрос не прошёл. Причина может быть в профиле, сервере, сети или выбранном сайте.", ProbeFailure.Reachability);
        }
    }

    private ProcessStartInfo StartInfo(params string[] arguments)
    {
        var info = new ProcessStartInfo(adapter.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(adapter.Executable)! };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("XRAY_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("SING_", StringComparison.OrdinalIgnoreCase)).ToList()) info.Environment.Remove(key);
        if (adapter.Kind == CoreKind.Xray) info.Environment["XRAY_LOCATION_ASSET"] = Path.GetDirectoryName(adapter.Executable)!;
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return info;
    }
    private void OnExited(object? sender, EventArgs args)
    {
        if (!stopping && ReferenceEquals(sender, process)) Set(ConnectionState.Failed, adapter.Label + " неожиданно остановился. Остановите подключение и запустите его заново.");
    }
    public void Stop()
    {
        stopping = true;
        if (process is not null)
        {
            process.Exited -= OnExited;
            TryKill(process);
            process.Dispose();
            process = null;
        }
        job?.Dispose(); job = null;
        Port = 0;
        Set(ConnectionState.Idle, "Подключение остановлено");
    }
    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(3000); } }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
    private static int FreePort(int preferred = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, preferred);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        catch (SocketException) when (preferred != 0)
        { throw new UserError("Локальный SOCKS-порт занят. Освободите его или перезапустите приложение, чтобы выбрать новый порт."); }
        finally { listener.Stop(); }
    }
    public void Dispose() => Stop();
}
