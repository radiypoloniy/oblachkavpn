using System.Windows;
using Microsoft.Win32;

namespace Oblachka;

public enum ThemeChoice { System, Light, Dark }

// Светлая и тёмная тема. Цвета живут в Themes/Light.xaml и Themes/Dark.xaml с одинаковыми
// ключами; интерфейс берёт их через DynamicResource, поэтому смена темы — это замена одного
// словаря в ресурсах приложения, без перезапуска и пересоздания окон.
//
// «Как в Windows» читает выбор системы для приложений и следит за его сменой.
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static ResourceDictionary? active;
    private static bool listening;

    public static ThemeChoice Choice { get; private set; } = ThemeChoice.System;
    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    public static void Apply(ThemeChoice choice)
    {
        Choice = choice;
        if (choice == ThemeChoice.System && !listening)
        {
            SystemEvents.UserPreferenceChanged += OnSystemChanged;
            listening = true;
        }
        Load(choice switch { ThemeChoice.Dark => true, ThemeChoice.Light => false, _ => SystemPrefersDark() });
    }

    // Темы и общие стили в ресурсах приложения: их видят все окна (главное, трей, диалоги).
    // Вызывается до создания первого окна; повторный вызов ничего не меняет.
    public static void EnsureLoaded()
    {
        var resources = Application.Current?.Resources;
        if (resources is null || active is not null) return;
        // Токены первыми: на шрифты, размеры и скругления ссылаются все остальные словари.
        foreach (var name in new[] { "Themes/Tokens.xaml", "Theme.xaml", "Design.xaml", "Sidebar.xaml", "Island.xaml" })
            resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Oblachka;component/" + name, UriKind.Relative) });
        Apply(Choice);
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    private static void OnSystemChanged(object sender, UserPreferenceChangedEventArgs args)
    {
        if (Choice != ThemeChoice.System || args.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => Load(SystemPrefersDark())));
    }

    private static void Load(bool dark)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;
        if (active is not null && dark == IsDark && resources.MergedDictionaries.Contains(active)) return;
        var next = new ResourceDictionary { Source = new Uri($"/Oblachka;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        // Новый словарь встаёт на место старого: ссылки DynamicResource переключаются сразу.
        var index = active is null ? -1 : resources.MergedDictionaries.IndexOf(active);
        if (index >= 0) resources.MergedDictionaries[index] = next; else resources.MergedDictionaries.Insert(0, next);
        active = next;
        IsDark = dark;
        Changed?.Invoke();
    }
}
