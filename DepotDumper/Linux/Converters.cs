using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DepotDumper.Linux;

/// <summary>Colours a log level the same way Windows' Logs page does (matches Themes/Dark.axaml).</summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#FF6F8FFF"));
    private static readonly IBrush Faint = new SolidColorBrush(Color.Parse("#FF5E6577"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#FFF5B94A"));
    private static readonly IBrush Danger = new SolidColorBrush(Color.Parse("#FFFF6B6B"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "DEBUG" => Faint,
        "WARNING" => Warn,
        "ERROR" or "CRITICAL" => Danger,
        _ => Accent,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
