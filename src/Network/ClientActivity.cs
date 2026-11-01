namespace Oblachka;

// Все операции пока выполняются в одном контексте приложения; окно не владеет их состоянием.
public sealed class ClientActivity
{
    public CancellationTokenSource? Operation { get; set; }
    public CancellationTokenSource? RecoveryWait { get; set; }
    public bool EngineOperation { get; set; }
    public bool BackgroundCheck { get; set; }
    public bool ManualStop { get; set; }
    public bool VmTestActive { get; set; }
}
