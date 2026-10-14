using System.Diagnostics;

namespace Oblachka;

// Связь окна с владельцем сетевой сессии: служба или помощник с UAC.
public interface IHelperLifetime : IAsyncDisposable
{
    NetworkHelperClient Client { get; }
    bool IsService { get; }
}

public static class NetworkHelperConnection
{
    // Установленная служба — основной путь. Помощник с UAC остаётся для переносной сборки
    // без установки. Если служба установлена, но не запущена, её сессии нет (при остановке
    // она её сняла), и помощник с ней не столкнётся.
    public static bool ServiceRunning => NetworkServiceControl.Query().Running;

    public static async Task<IHelperLifetime> ConnectAsync(CancellationToken token) =>
        NetworkServiceControl.Query() is { Running: true } status
            ? await ServiceHelperLifetime.ConnectAsync(status.ProcessId, token).ConfigureAwait(false)
            : await HelperProcessLifetime.ConnectAsync(NetworkHelperLauncher.Launch, token).ConfigureAwait(false);
}

public sealed class ServiceHelperLifetime : IHelperLifetime
{
    public NetworkHelperClient Client { get; }
    public bool IsService => true;
    private ServiceHelperLifetime(NetworkHelperClient client) { Client = client; }

    public static async Task<ServiceHelperLifetime> ConnectAsync(int servicePid, CancellationToken token)
    {
        // Служба слушается только своей установленной копии. Другая копия получила бы
        // молчаливый разрыв канала, поэтому причина называется заранее.
        using (var service = Process.GetProcessById(servicePid))
        {
            var directory = Path.GetDirectoryName(HelperPipeIdentity.ImagePath(service) ?? "");
            if (directory is not null && !HelperPipeIdentity.IsTrustedClient(Environment.ProcessPath, directory))
                throw new UserError("Системный режим работает через установленную " + ServiceBrand.Name +
                    ". Запустите её из меню «Пуск»: эта копия приложения — другая.");
        }
        var pipe = await HelperPipeClient.ConnectServiceAsync(servicePid, token).ConfigureAwait(false);
        return new(new NetworkHelperClient(pipe));
    }

    // Штатное закрытие снимает свою сессию (Release). Аварийное завершение окна до сюда
    // не доходит — тогда служба держит сессию и сеть закрыта до восстановления.
    public ValueTask DisposeAsync() => Client.DisposeAsync();
}

// Process creation is supplied by the trusted host (eventually the installed UAC launcher).
// No arbitrary executable path or command-line text is accepted through helper IPC.
public sealed class HelperProcessLifetime : IHelperLifetime
{
    private readonly Process process;
    public NetworkHelperClient Client { get; }
    public bool IsService => false;
    private HelperProcessLifetime(Process process, NetworkHelperClient client) { this.process = process; Client = client; }
    public static async Task<HelperProcessLifetime> ConnectAsync(Func<string, int, Process> launch, CancellationToken token)
    {
        var pipe = HelperPipeIdentity.NewPipeName();
        var process = launch(pipe, Environment.ProcessId);
        try
        {
            _ = process.SafeHandle;
            var client = new NetworkHelperClient(await HelperPipeClient.ConnectAsync(pipe, process, token));
            return new(process, client);
        }
        catch { process.Dispose(); throw; } // Helper's bounded startup/owner timeout owns its recovery.
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.DisposeAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { /* Never kill a helper while it may be restoring routes. */ }
        }
        finally { process.Dispose(); }
    }
}
