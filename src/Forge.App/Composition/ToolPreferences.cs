using Forge.App.Preferences;
using Forge.IO.Tools;

namespace Forge.App.Composition;

/// <summary>
/// Turns the editable preferences into the bounds the file and shell tools run with. Everything the model can send is
/// still clamped by <see cref="ToolOptions"/>' own hard limits; these values only move the defaults inside them, so a bad
/// <c>settings.json</c> cannot lift a cap.
/// </summary>
internal static class ToolPreferences
{
    public static ToolOptions From(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var defaults = ToolOptions.Default;
        return defaults with
        {
            ShellTimeoutSeconds = Math.Clamp(
                preferences.ToolsBashTimeoutSeconds,
                ToolOptions.MinShellTimeoutSeconds,
                ToolOptions.MaxShellTimeoutSeconds),
            // The fields are human-sized (kilobytes, result counts); the caps stay the tool's own hard limits.
            ShellCaptureBytes = Math.Clamp(
                preferences.ToolsBashCaptureKilobytes * 1024,
                ToolOptions.MinShellCaptureBytes,
                ToolOptions.MaxShellCaptureBytes),
            GrepMaxResults = Math.Clamp(
                preferences.ToolsGrepMaxResults,
                ToolOptions.MinGrepMaxResults,
                ToolOptions.MaxGrepMaxResults),
            IgnoredDirectories = ToolOptions.ParseIgnoredDirectories(preferences.ToolsIgnoredDirectories),
            ShellPath = Empty(preferences.ToolsShellPath),
            RipgrepPath = Empty(preferences.ToolsRipgrepPath),
        };
    }

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
