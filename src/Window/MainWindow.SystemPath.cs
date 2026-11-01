namespace Oblachka;

// Проверка «как у программ» под «Весь компьютер» (SystemPathProbe). Ядро может отвечать через
// свой SOCKS, а трафик компьютера при этом не проходить — так было в 0.13.1, и окно молчало.
// Порядок: две неудачные проверки подряд → туннель пересобирается один раз → не помогло —
// карточка «Интернет закрыт» с кнопкой выключения и уведомление в трее, а не «Защищено».
public partial class MainWindow
{
    private TunnelHealth pathHealth => runtime.Monitor.Path.Tunnel;
    private TunnelHealth directHealth => runtime.Monitor.Path.Direct;
    private bool pathRebuilt { get => runtime.Monitor.Path.Rebuilt; set => runtime.Monitor.Path.Rebuilt = value; }
    private bool pathBroken { get => runtime.Monitor.Path.Broken; set => runtime.Monitor.Path.Broken = value; }
    private string pathProblem { get => runtime.Monitor.Path.Problem; set => runtime.Monitor.Path.Problem = value; }
    private bool pathDirectOnly => runtime.Monitor.Path.DirectOnly;

    private void SystemPathHealthy()
    {
        pathRebuilt = false;
        if (!pathBroken) return;
        pathBroken = false; networkClosed = false;
        DetailText.Text = "Связь через туннель восстановилась.";
        RefreshHome();
    }

    // Ручное подключение и остановка начинают с чистого листа.
    private void ResetSystemPath()
    {
        runtime.Monitor.Path.Reset();
    }
}
