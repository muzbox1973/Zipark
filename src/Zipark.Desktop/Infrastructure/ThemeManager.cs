using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Zipark.Settings;
using Microsoft.Win32;

namespace Zipark.Desktop.Infrastructure;

/// <summary>Light/dark palettes (Themes/Light.xaml, Themes/Dark.xaml) with "follow Windows" support.</summary>
public static class ThemeManager
{
    private static ResourceDictionary? _current;
    public static bool IsDark { get; private set; }
    public static ThemeMode Mode { get; private set; }

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        IsDark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => SystemPrefersDark(),
        };
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Zipark;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml") };
        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Insert(0, dict);
        _current = dict;
        foreach (Window w in System.Windows.Application.Current.Windows) ApplyTitleBar(w);
    }

    public static void Watch()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Mode == ThemeMode.System)
                System.Windows.Application.Current.Dispatcher.Invoke(() => Apply(ThemeMode.System));
        };
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Dark title bar on Windows 10 20H1+ / Windows 11.</summary>
    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
