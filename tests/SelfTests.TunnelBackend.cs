namespace Oblachka;

public static partial class SelfTests
{
    // Реальный сетевой backend проверяется на подменном ядре и подменном списке
    // интерфейсов: сеть компьютера при этом не меняется. Сценарии взяты из того, что
    // показала проверка на VM.
    private sealed class FakeTunnelProcess : ITunnelProcess
    {
        public bool IsRunning { get; private set; } = true;
        public int Disposals;
        public void Dispose() { Disposals++; IsRunning = false; }
    }

    private static async Task TunnelBackendChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        var peers = new[] { "203.0.113.7" };
        NetworkLeaseRequest Request(Guid id) => new(1, id, NetworkLeaseAction.Acquire, 10808, peers);
        static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

        await check("Tunnel session owns one change: the core process with its interface", () => Sync(() =>
        {
            var lease = NetworkHelperContracts.Validate(Request(Guid.NewGuid()));
            var backend = new WindowsTunnelBackend((_, _) => new FakeTunnelProcess(), _ => false, delay: NoDelay);
            var changes = backend.Prepare(lease);
            Assert(changes.Count == 1 && changes[0].Kind == NetworkResourceKind.TunnelInterface);
            Assert(changes[0].ResourceId == WindowsTunnelBackend.InterfaceName(lease.LeaseId));
            Assert(changes[0].ResourceId.StartsWith(WindowsTunnelBackend.InterfacePrefix, StringComparison.Ordinal));
            Assert(changes[0].Before is null && changes[0].After.Contains("exclude=203.0.113.7"));
            // Имя интерфейса выводится из сессии, а не принимается снаружи.
            Assert(WindowsTunnelBackend.InterfaceName(lease.LeaseId) != WindowsTunnelBackend.InterfaceName(Guid.NewGuid()));
            // Запись из чужой сессии не распоряжается нашим интерфейсом.
            ExpectUserError(() => backend.Apply(Guid.NewGuid(), changes[0]));
            ExpectUserError(() => backend.Restore(Guid.NewGuid(), changes[0]));
        }));
        await check("Full session starts the core, waits for the interface and removes it on release", () => Sync(() =>
        {
            var id = Guid.NewGuid();
            var name = WindowsTunnelBackend.InterfaceName(id);
            var up = false;
            var core = new FakeTunnelProcess();
            // Интерфейс живёт вместе с процессом: так ведёт себя wintun, это видно на стенде.
            var backend = new WindowsTunnelBackend((_, _) => { up = true; return core; },
                n => up && core.IsRunning && n == name, delay: NoDelay);
            var journal = new NetworkSessionJournal(Path.Combine(root, "tun-backend.json"));
            var session = new NetworkSession(journal, backend);

            var applied = session.Acquire(Request(id));
            Assert(applied.State == NetworkSessionState.Applied && up && core.Disposals == 0);
            // Пока сессия жива, вторая не начинается.
            ExpectUserError(() => session.Acquire(Request(Guid.NewGuid())));

            // Останавливаем так же, как это делает ядро: интерфейс исчезает вместе с процессом.
            var released = session.Release(id);
            Assert(released.State == NetworkSessionState.Released && core.Disposals == 1);
            Assert(released.Changes.All(c => c.State == NetworkChangeState.Restored));
        }));
        await check("Foreign interface with our name is a conflict, not something to tear down", () => Sync(() =>
        {
            var id = Guid.NewGuid();
            var backend = new WindowsTunnelBackend((_, _) => new FakeTunnelProcess(), _ => true, delay: NoDelay);
            var lease = NetworkHelperContracts.Validate(Request(id));
            var change = backend.Prepare(lease)[0];
            Assert(backend.Apply(id, change) == NetworkWriteResult.Conflict);
        }));
        await check("Core that dies with its owner leaves a session that releases cleanly", () => Sync(() =>
        {
            // На стенде журнал остался в Running после убийства процесса, а интерфейс Windows
            // убрала сама. Восстановление обязано считать отсутствие интерфейса успехом.
            var id = Guid.NewGuid();
            var backend = new WindowsTunnelBackend((_, _) => new FakeTunnelProcess(), _ => false, delay: NoDelay);
            var lease = NetworkHelperContracts.Validate(Request(id));
            var change = backend.Prepare(lease)[0] with { State = NetworkChangeState.Applied };
            Assert(backend.Restore(id, change) == NetworkWriteResult.Unchanged);
        }));
        await check("Interface that survives the stop is reported, not silently accepted", () => Sync(() =>
        {
            // Застрявший адаптер — повод для RecoveryRequired, а не для тихого «всё хорошо».
            var id = Guid.NewGuid();
            var core = new FakeTunnelProcess();
            var up = false;
            var backend = new WindowsTunnelBackend((_, _) => { up = true; return core; }, _ => up,
                TimeSpan.FromMilliseconds(30), NoDelay);
            var lease = NetworkHelperContracts.Validate(Request(id));
            var change = backend.Prepare(lease)[0];
            Assert(backend.Apply(id, change) == NetworkWriteResult.Changed);
            // Процесс остановлен, но адаптер не исчез.
            var failed = false;
            try { backend.Restore(id, change with { State = NetworkChangeState.Applied }); } catch (IOException) { failed = true; }
            Assert(failed && core.Disposals == 1);
        }));
        await check("Core that never raises an interface is stopped instead of left behind", () => Sync(() =>
        {
            var id = Guid.NewGuid();
            var core = new FakeTunnelProcess();
            var backend = new WindowsTunnelBackend((_, _) => core, _ => false, TimeSpan.FromMilliseconds(30), NoDelay);
            var lease = NetworkHelperContracts.Validate(Request(id));
            var change = backend.Prepare(lease)[0];
            ExpectUserError(() => backend.Apply(id, change));
            Assert(core.Disposals == 1);
        }));
        await check("Session recovery after an abandoned journal stops the core and releases", () => Sync(() =>
        {
            var id = Guid.NewGuid();
            var up = false;
            var core = new FakeTunnelProcess();
            var backend = new WindowsTunnelBackend((_, _) => { up = true; return core; }, _ => up, delay: NoDelay);
            var journal = new NetworkSessionJournal(Path.Combine(root, "tun-recovery.json"));
            var session = new NetworkSession(journal, backend);
            session.Acquire(Request(id));
            // Ядро убито вместе с владельцем: журнал остался в Applied, интерфейса уже нет.
            // Это ровно то, что показал стенд, и путь восстановления helper при запуске.
            up = false;
            var recovered = session.Recover();
            Assert(recovered!.State == NetworkSessionState.Released);
            Assert(recovered.Changes.All(c => c.State == NetworkChangeState.Restored));
        }));
        await Task.CompletedTask;
    }
}
