using System.Windows;
using System.Windows.Controls;

namespace Oblachka.Views;

public partial class AddMenu : UserControl
{
    public AddMenu()
    {
        InitializeComponent();
        KeyInput.TextChanged += (_, _) => AddKeyButton.IsEnabled = KeyInput.Text.Trim().Length > 0;
        // «Вставить из буфера» кладёт текст в поле: видно, что именно добавится.
        PasteButton.Click += (_, _) =>
        {
            try { if (Clipboard.ContainsText()) KeyInput.Text = Clipboard.GetText().Trim(); }
            catch (System.Runtime.InteropServices.ExternalException) { }
            KeyInput.Focus(); KeyInput.CaretIndex = KeyInput.Text.Length;
        };
    }
}
