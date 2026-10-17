using System.Windows;

namespace Oblachka;

// Присоединяемые свойства для стилей нового интерфейса: у Button нет своего скругления,
// а шаблон кнопки в Design.xaml берёт его отсюда.
public static class Ui
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(0)));
    public static CornerRadius GetCornerRadius(DependencyObject target) => (CornerRadius)target.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject target, CornerRadius value) => target.SetValue(CornerRadiusProperty, value);

    // Выбранный пункт боковой панели (раздел, шестерёнка): стиль подсвечивает его, не меняя размер.
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.RegisterAttached(
        "Selected", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));
    public static bool GetSelected(DependencyObject target) => (bool)target.GetValue(SelectedProperty);
    public static void SetSelected(DependencyObject target, bool value) => target.SetValue(SelectedProperty, value);
}
