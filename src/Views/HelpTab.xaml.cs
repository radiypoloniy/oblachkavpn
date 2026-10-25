using System.Windows;
using System.Windows.Controls;

namespace Oblachka.Views;

public partial class HelpTab : UserControl
{
    public HelpTab()
    {
        InitializeComponent();
        SizeChanged += (_, args) => Arrange(args.NewSize.Width < 620);
        Arrange(false);
    }

    // Карточки «Что случилось?»: две в ряд, в узком окне — по одной. Высота строки — по её карточкам.
    private bool? single;
    private void Arrange(bool narrow)
    {
        if (single == narrow) return;
        single = narrow;
        var cards = SymptomGrid.Children.OfType<UIElement>().ToArray();
        var columns = narrow ? 1 : 2;
        SymptomGrid.ColumnDefinitions.Clear(); SymptomGrid.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++) SymptomGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (var r = 0; r < (cards.Length + columns - 1) / columns; r++) SymptomGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < cards.Length; i++) { Grid.SetRow(cards[i], i / columns); Grid.SetColumn(cards[i], i % columns); }
        // «Проверить сеть»: в узком окне кнопка — под текстом, иначе заголовок ломался на четыре строки.
        DockPanel.SetDock(NetworkCheckButton, narrow ? Dock.Bottom : Dock.Right);
        NetworkCheckButton.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        NetworkCheckButton.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }
}
