using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Oblachka;

// Диалоги в стиле нового дизайна: скругление, мягкая тень, цвета темы, кнопки как в окне.
// Вопрос с ответом «да/нет» (Confirm) и поле ввода (Ask). Enter — главное действие, Esc — отмена,
// перетаскивание за заголовок.
public static class AppDialog
{
    public static bool Confirm(Window? owner, string title, string text, string yes, string no = "Отмена", bool danger = false) =>
        Show(owner, title, text, null, yes, no, danger) is not null;

    // Возвращает введённое или null при отмене. note — пояснение под полем.
    public static string? Ask(Window? owner, string title, string text, string hint, string yes, string? initial = null, string? note = null,
        bool multiline = false, int maxLength = 20 * 1024)
    {
        var box = new TextBox
        {
            Text = initial ?? "", Tag = hint, MaxLength = maxLength, FontFamily = (FontFamily)Application.Current.FindResource("MonoFont"), FontSize = 13,
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, Height = multiline ? 110 : 42,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center, Padding = new Thickness(12, multiline ? 10 : 0, 12, 0),
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled, Margin = new Thickness(0, 14, 0, 0)
        };
        box.Style = (Style)Application.Current.FindResource("FieldBox");
        var extra = new StackPanel();
        extra.Children.Add(box);
        if (note is not null) extra.Children.Add(Small(note, new Thickness(2, 8, 0, 0)));
        var result = Show(owner, title, text, extra, yes, "Отмена", false, box);
        var value = result is null ? null : box.Text;
        box.Clear();
        return value;
    }

    private static object? Show(Window? owner, string title, string text, UIElement? extra, string yes, string no, bool danger, Control? focus = null) =>
        Build(owner, title, text, extra, yes, no, danger, focus).ShowDialog() == true ? true : null;

    // Окно диалога без показа: для превью и проверок.
    internal static Window Build(Window? owner, string title, string text, UIElement? extra, string yes, string no, bool danger, Control? focus = null)
    {
        var dialog = new Window
        {
            Owner = owner is { IsVisible: true } ? owner : null, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, SizeToContent = SizeToContent.Height, Width = 452, ShowInTaskbar = owner is not { IsVisible: true }, Title = title,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont")
        };
        var body = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        var heading = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "Tx");
        body.Children.Add(heading);
        var message = new TextBlock { Text = text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(0, 8, 0, 0) };
        message.SetResourceReference(TextBlock.ForegroundProperty, "Mu");
        body.Children.Add(message);
        if (extra is not null) body.Children.Add(extra);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var cancel = new Button { Content = no, IsCancel = true, MinWidth = 104, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Style = (Style)Application.Current.FindResource("BtnOutline");
        var ok = new Button { Content = yes, IsDefault = true, MinWidth = 120, Padding = new Thickness(18, 0, 18, 0) };
        ok.Style = (Style)Application.Current.FindResource("BtnPrimary");
        // Опасное действие — мягкая красная плашка, как метка «блок»: читается в обеих темах.
        if (danger) { ok.SetResourceReference(Control.BackgroundProperty, "RB"); ok.SetResourceReference(Control.ForegroundProperty, "RF"); }
        Ui.SetCornerRadius(ok, new CornerRadius(12)); Ui.SetCornerRadius(cancel, new CornerRadius(12));
        ok.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        body.Children.Add(buttons);

        var card = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Margin = new Thickness(18), Child = body,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.16, Color = Color.FromRgb(0x0D, 0x1B, 0x30) } };
        card.SetResourceReference(Border.BackgroundProperty, "Bg");
        card.SetResourceReference(Border.BorderBrushProperty, "Divider");
        card.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox && e.ButtonState == MouseButtonState.Pressed) dialog.DragMove(); };
        dialog.Content = card;
        // Фокус — в поле ввода; у вопроса без поля Enter и Esc работают и без фокуса на кнопке.
        if (focus is not null) dialog.Loaded += (_, _) => focus.Focus();
        return dialog;
    }

    private static TextBlock Small(string text, Thickness margin)
    {
        var block = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = margin };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Mu2");
        return block;
    }
}
