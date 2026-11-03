using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Zipark.Core;
using Zipark.Crypto;

namespace Zipark.Desktop.Infrastructure;

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long l => ByteSize.Format(l),
        int i => ByteSize.Format(i),
        double d => ByteSize.Format((long)d),
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class SpeedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d && d > 0 ? ByteSize.Format((long)d) + "/s" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class EtaConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is TimeSpan t
        ? t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss")
        : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool v = value switch
        {
            bool b => b,
            null => false,
            string s => s.Length > 0,
            int i => i != 0,
            _ => true,
        };
        return v ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is Visibility v && (v == Visibility.Visible) ^ Invert;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Maps an enum value to a localized string "s.{prefix}.{Value}".</summary>
public sealed class EnumLocConverter : IValueConverter
{
    public string Prefix { get; set; } = "";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? "" : Loc.T($"s.{Prefix}.{value}");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StrengthBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is StrengthLevel l ? l switch
        {
            StrengthLevel.VeryWeak or StrengthLevel.Weak => "Brush.Danger",
            StrengthLevel.Fair => "Brush.Warning",
            _ => "Brush.Success",
        } : "Brush.Border";
        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NoticeBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            NoticeKind.Success => "Brush.Success",
            NoticeKind.Warning => "Brush.Warning",
            NoticeKind.Error => "Brush.Danger",
            _ => "Brush.Accent",
        };
        return System.Windows.Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is not null) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime d ? d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
