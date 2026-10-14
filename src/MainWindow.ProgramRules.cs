namespace Oblachka;

public partial class MainWindow
{
    private readonly List<ProgramRule> programRules = new();
    private void WireProgramRules()
    {
        Routes.AddProgramButton.Click += (_, _) => AddProgramRule(Routes.ProgramName.Text, SiteAction.Tunnel);
        Routes.ProgramName.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) AddProgramRule(Routes.ProgramName.Text, SiteAction.Tunnel); };
    }

    private void AddProgramRule(string executable, SiteAction action)
    {
        try
        {
            var rule = ProgramRoutingPlan.Create(executable, action);
            ProgramRoutingPlan.Validate(programRules.Append(rule).ToArray());
            programRules.Add(rule); Routes.ProgramName.Clear(); RefreshRules(); QueueStateSave();
        }
        catch (UserError ex) { DetailText.Text = ex.Message; }
    }
    private async Task ValidateProgramDraftAsync(CancellationToken token)
    {
        if (programRules.Count == 0) return;
        var request = new NetworkLeaseRequest(1, Guid.NewGuid(), NetworkLeaseAction.Acquire, 10808, ["192.0.2.1"]);
        var adapter = new CoreAdapter(CoreKind.SingBox, Path.Combine(AppContext.BaseDirectory, "tools", "sing-box.exe"));
        await CoreConfigurationCheck.ValidateAsync(adapter, ProgramRoutingPlan.Compile(request, programRules), token);
    }
}
