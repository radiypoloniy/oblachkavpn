namespace Oblachka;

public static partial class SelfTests
{
    private static async Task ClientRuntimeChecksAsync(Func<string, Func<Task>, Task> check)
    {
        await check("Retired borrowed sessions detach all core/helper events without disposing application resources", async () =>
        {
            var calls = new List<string>(); var core = new SessionCore(calls); var helper = new SessionHelper(calls);
            for (var i = 0; i < 1000; i++)
            {
                var session = new SystemTunnelSession(core, helper, ownsResources: false);
                Assert(core.Subscribers == 1 && helper.Subscribers == 2);
                await session.DisposeAsync(); await session.DisposeAsync();
                Assert(core.Subscribers == 0 && helper.Subscribers == 0);
            }
            Assert(core.Disposals == 0 && helper.Disposals == 0 && calls.Count(c => c == "helper:release") == 1000);
            await using var next = new SystemTunnelSession(core, helper, ownsResources: false);
            await next.StartAsync(new(Profiles.Parse(link), new[] { "192.0.2.1" }), CancellationToken.None);
            Assert(next.State == SystemTunnelState.Running);
        });
        await check("Failed session cleanup still removes subscriptions and releases owned resources once", async () =>
        {
            var calls = new List<string>(); var core = new SessionCore(calls); var helper = new SessionHelper(calls);
            var session = new SystemTunnelSession(core, helper);
            using var cancel = new CancellationTokenSource(); helper.DuringAcquire = cancel.Cancel;
            try { await session.StartAsync(new(Profiles.Parse(link), new[] { "192.0.2.1" }), cancel.Token); }
            catch (OperationCanceledException) { }
            for (var i = 0; i < 2; i++)
                try { await session.DisposeAsync(); } catch (IOException) { }
            Assert(core.Subscribers == 0 && helper.Subscribers == 0 && core.Disposals == 1 && helper.Disposals == 1);
        });
        await check("Profile state supports selection and filtering without WPF and survives view detachment", () => Sync(() =>
        {
            var calls = new List<string>(); var core = new SessionCore(calls);
            using var runtime = new ClientRuntime(core);
            var a = Profiles.Parse(link); var b = Profiles.Parse(link.Replace("#Test", "#Second"));
            var state = runtime.Profiles; var picker = state.Picker;
            state.Imported = [a, b]; picker.ItemsSource = state.Imported;
            var changes = 0; EventHandler view = (_, _) => changes++;
            picker.SelectionChanged += view;
            picker.SelectedIndex = 1; Assert(ReferenceEquals(picker.SelectedItem, b) && picker.SelectedIndex == 1);
            picker.SelectedItem = b; Assert(changes == 1);
            state.Selected = b; picker.ItemsSource = [a]; picker.SelectedItem = b;
            Assert(picker.SelectedIndex == -1 && picker.SelectedItem is null && ReferenceEquals(state.Selected, b));
            picker.ItemsSource = state.Imported; picker.SelectedItem = state.Selected;
            picker.SelectionChanged -= view;
            picker.SelectedIndex = 0;
            Assert(changes == 3 && core.Disposals == 0 && ReferenceEquals(state.Selected, b));
        }));
    }
}
