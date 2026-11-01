namespace Oblachka;

// Выбор сервера без скрытого WPF ListBox. Фильтрация списка не меняет активное подключение.
public sealed class ProfilePickerState
{
    private IReadOnlyList<Profile> items = Array.Empty<Profile>();
    private Profile? selected;
    public bool IsEnabled { get; set; } = true;
    public IReadOnlyList<Profile> Items => items;
    public IReadOnlyList<Profile> ItemsSource
    {
        get => items;
        set
        {
            items = value ?? Array.Empty<Profile>();
            if (selected is not null && !items.Contains(selected)) SelectedItem = null;
        }
    }
    public Profile? SelectedItem
    {
        get => selected;
        set
        {
            if (value is not null && !items.Contains(value)) return;
            if (ReferenceEquals(selected, value)) return;
            selected = value;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public int SelectedIndex
    {
        get
        {
            if (selected is not null)
                for (var i = 0; i < items.Count; i++) if (Equals(items[i], selected)) return i;
            return -1;
        }
        set => SelectedItem = value >= 0 && value < items.Count ? items[value] : null;
    }
    public event EventHandler? SelectionChanged;
}
