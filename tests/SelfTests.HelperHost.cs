namespace Oblachka;

public static partial class SelfTests
{
    // Привилегированная половина системного режима. Проверяется то, что можно проверить
    // без прав администратора: разбор аргументов, отказы и состав команды запуска.
    // Сеть при этом не меняется и ядро не запускается.
    private static async Task HelperHostChecksAsync(Func<string, Func<Task>, Task> check)
    {
        var pipe = HelperPipeIdentity.NewPipeName();
        await check("Helper accepts only a pipe name and an owner, nothing else", () => Sync(() =>
        {
            var parsed = NetworkHelperHost.ParseArguments(new[] { NetworkHelperHost.Argument, pipe, "4242" });
            Assert(parsed.PipeName == pipe && parsed.OwnerProcessId == 4242);
            // Ни пути к ядру, ни конфигурации, ни адресов узлов в аргументах быть не может.
            ExpectUserError(() => NetworkHelperHost.ParseArguments(new[] { NetworkHelperHost.Argument, pipe, "4242", "C:\\tools\\sing-box.exe" }));
            ExpectUserError(() => NetworkHelperHost.ParseArguments(new[] { NetworkHelperHost.Argument, pipe }));
            ExpectUserError(() => NetworkHelperHost.ParseArguments(new[] { "--что-то-другое", pipe, "4242" }));
            ExpectUserError(() => NetworkHelperHost.ParseArguments(new[] { NetworkHelperHost.Argument, "чужой-канал", "4242" }));
            ExpectUserError(() => NetworkHelperHost.ParseArguments(new[] { NetworkHelperHost.Argument, pipe, "0" }));
            ExpectUserError(() => NetworkHelperHost.ParseArguments(new[] { NetworkHelperHost.Argument, pipe, "-1" }));
            ExpectUserError(() => NetworkHelperHost.ParseArguments(new[] { NetworkHelperHost.Argument, pipe, "не-число" }));
        }));
        await check("Helper refuses to serve without administrator rights or without an owner", () => Sync(() =>
        {
            var started = 0;
            ITunnelProcess Core(string _) { started++; return new FakeTunnelProcess(); }
            // Без прав администратора помощник не обслуживает никого и ядро не запускает.
            Assert(NetworkHelperHost.Run(new[] { NetworkHelperHost.Argument, pipe, "4242" }, () => false, Core) == 2);
            // Несуществующий владелец: обслуживать некого.
            Assert(NetworkHelperHost.Run(new[] { NetworkHelperHost.Argument, pipe, "2147483646" }, () => true, Core) == 2);
            // Плохие аргументы отвергаются до любой проверки прав.
            Assert(NetworkHelperHost.Run(new[] { NetworkHelperHost.Argument, "плохой-канал", "1" }, () => true, Core) == 2);
            Assert(started == 0);
        }));
        await check("Helper is launched with explicit elevation and no free-form arguments", () => Sync(() =>
        {
            var start = NetworkHelperLauncher.Describe(pipe, 4242);
            Assert(start.Verb == "runas" && start.UseShellExecute);
            Assert(start.FileName.EndsWith("Oblachka.exe", StringComparison.Ordinal));
            Assert(start.ArgumentList.Count == 3);
            Assert(start.ArgumentList[0] == NetworkHelperHost.Argument && start.ArgumentList[1] == pipe && start.ArgumentList[2] == "4242");
            // Имя канала и владелец проверяются до запуска процесса.
            ExpectUserError(() => NetworkHelperLauncher.Launch("чужой-канал", 4242));
            ExpectUserError(() => NetworkHelperLauncher.Launch(pipe, 0));
        }));
        await check("Helper journal lives in the client data folder, not next to the build", () => Sync(() =>
        {
            Assert(NetworkHelperHost.JournalPath.StartsWith(ClientDataPaths.Root, StringComparison.OrdinalIgnoreCase));
            Assert(!NetworkHelperHost.JournalPath.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
        }));
        await Task.CompletedTask;
    }
}
