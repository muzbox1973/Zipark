using System.Globalization;
using System.Windows;
using Zipark.Core;

namespace Zipark.Desktop.Infrastructure;

/// <summary>
/// Runtime language switching. Strings live in Strings/ko.xaml and Strings/en.xaml (keys "s.*"),
/// referenced from XAML with DynamicResource so the UI updates without a restart.
/// </summary>
public static class Loc
{
    private static ResourceDictionary? _current;
    public static string Language { get; private set; } = "ko";
    public static event Action? Changed;

    public static void Apply(string language)
    {
        Language = language == "en" ? "en" : "ko";
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Zipark;component/Strings/{Language}.xaml") };
        var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Add(dict);
        _current = dict;
        var culture = CultureInfo.GetCultureInfo(Language == "ko" ? "ko-KR" : "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        Changed?.Invoke();
    }

    public static string T(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    public static string Error(ErrorCode code) => T("s.err." + code);

    public static string Describe(Exception ex) => ex switch
    {
        ZiparkException fx when fx.Code != ErrorCode.Unknown => Error(fx.Code) + (string.IsNullOrWhiteSpace(fx.Message) ? "" : "\n" + fx.Message),
        _ => ex.Message,
    };
}
