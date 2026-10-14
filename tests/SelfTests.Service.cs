using System.Diagnostics;

namespace Oblachka;

public static partial class SelfTests
{
    // Сетевая служба: кому она подчиняется, где её можно ставить и что она делает с сессией
    // владельца, который пропал. Служба при этом не устанавливается: сервер канала крутится
    // в этом процессе на подменном backend.
    private static async Task ServiceChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Service trusts only a client from its own protected folder", () => Sync(() =>
        {
            const string folder = @"C:\Program Files\Oblachka";
            Assert(HelperPipeIdentity.IsTrustedClient(@"C:\Program Files\Oblachka\Oblachka.exe", folder));
            Assert(HelperPipeIdentity.IsTrustedClient(@"c:\program files\oblachka\OBLACHKA.EXE", folder + "\\"));
            Assert(HelperPipeIdentity.IsTrustedClient(@"C:\Program Files\Oblachka\Oblachka.Tests.exe", folder));
            // Переносная копия, вложенная папка, чужая программа рядом — нет.
            Assert(!HelperPipeIdentity.IsTrustedClient(@"C:\Users\user\Desktop\Oblachka\Oblachka.exe", folder));
            Assert(!HelperPipeIdentity.IsTrustedClient(@"C:\Program Files\Oblachka\tools\Oblachka.exe", folder));
            Assert(!HelperPipeIdentity.IsTrustedClient(@"C:\Program Files\Oblachka\powershell.exe", folder));
            Assert(!HelperPipeIdentity.IsTrustedClient(@"C:\Program Files\Oblachka2\Oblachka.exe", folder));
            Assert(!HelperPipeIdentity.IsTrustedClient(null, folder));
        }));
        await check("Service registers only from Program Files and keeps its journal in ProgramData", () => Sync(() =>
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            Assert(NetworkServiceControl.IsProtectedLocation(Path.Combine(programFiles, "Oblachka")));
            // Сосед с похожим началом имени — не Program Files.
            Assert(!NetworkServiceControl.IsProtectedLocation(programFiles + "Evil\\Oblachka"));
            Assert(!NetworkServiceControl.IsProtectedLocation(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
            Assert(!NetworkServiceControl.IsProtectedLocation(AppContext.BaseDirectory) || AppContext.BaseDirectory.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase));
            Assert(NetworkService.JournalPath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase));
            Assert(NetworkService.JournalPath != NetworkHelperHost.JournalPath);
        }));
        await check("Recover command carries no network parameters", () => Sync(() =>
        {
            HelperProtocol.Validate(new(1, Guid.NewGuid(), HelperOperation.Recover, null));
            ExpectUserError(() => HelperProtocol.Validate(new(1, Guid.NewGuid(), HelperOperation.Recover, NetworkAcquire())));
        }));
        await check("Service keeps a lost owner's session closed until the next owner recovers or replaces it", async () =>
        {
            var journal = new MemoryNetworkJournal(); var backend = new FakeNetworkBackend();
            var session = new NetworkSession(journal, backend);
            using var owner = Process.GetCurrentProcess(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            async Task<(HelperPipeClient Client, Task Served)> Connect()
            {
                var name = HelperPipeIdentity.NewPipeName();
                var pipe = HelperPipeIdentity.CreateServer(name);
                var served = Task.Run(async () =>
                {
                    using (pipe)
                    {
                        await pipe.WaitForConnectionAsync(timeout.Token);
                        await HelperPipeServer.ServeAsync(session, pipe, owner, null, null, null, timeout.Token);
                    }
                });
                return (await HelperPipeClient.ConnectAsync(name, owner, timeout.Token), served);
            }

            // Владелец поднял сессию и пропал без Release: защита остаётся.
            var (first, served) = await Connect();
            var lost = NetworkAcquire();
            Assert((await first.SendAsync(HelperOperation.Acquire, lost, timeout.Token)).State == NetworkSessionState.Applied);
            await first.DisposeAsync(); await PipeClosedAsync(served);
            Assert(journal.Record?.State == NetworkSessionState.Applied && !backend.Original);

            // Следующий владелец не может снять её как свою, но может как оставленную.
            var (second, served2) = await Connect();
            var foreign = new NetworkLeaseRequest(1, lost.LeaseId, NetworkLeaseAction.Release, 0, Array.Empty<string>());
            Assert((await second.SendAsync(HelperOperation.Release, foreign, timeout.Token)).Failure == HelperFailure.InvalidRequest);
            Assert(!backend.Original);
            Assert((await second.SendAsync(HelperOperation.Recover, null, timeout.Token)).State == NetworkSessionState.Released);
            Assert(backend.Original);

            // Новое подключение поверх оставленной сессии сначала снимает её.
            Assert((await second.SendAsync(HelperOperation.Acquire, NetworkAcquire(), timeout.Token)).State == NetworkSessionState.Applied);
            await second.DisposeAsync(); await PipeClosedAsync(served2);
            var (third, served3) = await Connect();
            var replacement = NetworkAcquire();
            Assert((await third.SendAsync(HelperOperation.Acquire, replacement, timeout.Token)).State == NetworkSessionState.Applied);
            Assert(journal.Record?.LeaseId == replacement.LeaseId);
            // Свою живую сессию через Recover не снять: только Release.
            Assert((await third.SendAsync(HelperOperation.Recover, null, timeout.Token)).Failure == HelperFailure.InvalidRequest);
            var release = new NetworkLeaseRequest(1, replacement.LeaseId, NetworkLeaseAction.Release, 0, Array.Empty<string>());
            Assert((await third.SendAsync(HelperOperation.Release, release, timeout.Token)).State == NetworkSessionState.Released);
            Assert(backend.Original);
            await third.DisposeAsync(); await PipeClosedAsync(served3);
        });
    }
}
