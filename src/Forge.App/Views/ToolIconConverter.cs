using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// Resolves the timeline node's glyph for a tool kind against the icon dictionary, so a tool card can carry the same icon as
/// the node on the axis. A missing key yields no geometry (an empty icon), never a crash.
/// </summary>
internal sealed class ToolIconConverter : IValueConverter
{
    public static readonly ToolIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not TimelineNodeKind kind)
        {
            return null;
        }

        var key = TimelineNodeMap.IconKey(kind, false);
        return Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var geometry)
            ? geometry
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
