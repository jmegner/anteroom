using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Anteroom.App.Models;

namespace Anteroom.App.UI;

/// <summary>Accent colour for a tab's status bar and dot, by what the session is waiting for.</summary>
public sealed class AttentionBrushConverter : IValueConverter
{
    public static readonly SolidColorBrush Permission = Frozen(0xF2, 0xA0, 0x4B);
    public static readonly SolidColorBrush Idle = Frozen(0x6E, 0xA8, 0xF0);
    public static readonly SolidColorBrush TurnComplete = Frozen(0x7A, 0xD1, 0x9A);
    public static readonly SolidColorBrush Calm = Frozen(0x55, 0x5A, 0x63);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as AttentionKind?) switch
        {
            AttentionKind.Permission => Permission,
            AttentionKind.Idle => Idle,
            AttentionKind.TurnComplete => TurnComplete,
            _ => Calm
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Collapses an element when its bound string is null or blank.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when true; pass "invert" as the parameter to flip it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is true;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Chevron glyph for a tab's expand toggle.</summary>
public sealed class ExpandGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "▴" : "▾";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
