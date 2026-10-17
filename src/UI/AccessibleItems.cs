using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Oblachka;

// Список, у строк которого кнопки видны экранному диктору и UIA. Обычный ItemsControl отдаёт
// строку как пустой элемент данных: кнопки внутри неё («Через VPN», «Удалить правило») ни
// диктору, ни автоматическим проверкам не достать. Здесь дерево доступности — визуальное.
public sealed class AccessibleItems : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
