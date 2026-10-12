using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CraftPlanner.App.Services;
using CraftPlanner.Core.Calculation;

namespace CraftPlanner.App.Converters;

public sealed class Base64ToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => IconService.Decode(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : true;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Inverse { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if (Inverse) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>null または空文字なら Collapsed。Inverse=true なら逆。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Inverse { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value == null || (value is string s && s.Length == 0);
        if (Inverse) isNull = !isNull;
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class MemoColorConverter : IValueConverter
{
    public static readonly Dictionary<string, Color> Colors = new()
    {
        ["yellow"] = Color.FromRgb(0xFF, 0xF5, 0xB1),
        ["blue"] = Color.FromRgb(0xCF, 0xE4, 0xFF),
        ["green"] = Color.FromRgb(0xD4, 0xF5, 0xD0),
        ["pink"] = Color.FromRgb(0xFF, 0xD6, 0xE0),
        ["gray"] = Color.FromRgb(0xE5, 0xE7, 0xEB),
        ["orange"] = Color.FromRgb(0xFF, 0xE0, 0xC2),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string ?? "yellow";
        var color = Colors.TryGetValue(key, out var c) ? c : Colors["yellow"];
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>制作可能の判定を枠線色にする。</summary>
public sealed class CraftabilityBrushConverter : IValueConverter
{
    private static readonly Brush None = Freeze(new SolidColorBrush(Color.FromRgb(0xB0, 0xB6, 0xBF)));
    private static readonly Brush Covered = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)));
    private static readonly Brush Direct = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x9E, 0x4A)));
    private static readonly Brush Indirect = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0xC9, 0x8F)));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Craftability.Covered => Covered,
        Craftability.Direct => Direct,
        Craftability.Indirect => Indirect,
        _ => None,
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }
}

public sealed class ZoomPercentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? $"{Math.Round(d * 100)}%" : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
