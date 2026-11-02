namespace Oblachka;

public partial class MainWindow
{
    // Скрытые узлы ещё принадлежат подпискам; их замеры нужны при возврате в список.
    // Работающий узел может пережить обновление или удаление своей подписки.
    private void TrimMeasurements()
    {
        runtime.Profiles.TrimMeasurements();
    }
}
