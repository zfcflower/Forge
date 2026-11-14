using Forge.App.Preferences;
using MermaidKinds = Mermaider.Models.DiagramTypes;

namespace Forge.App.Services;

/// <summary>
/// The live preferences a Mermaid block reads, ambient like <c>ToolImageSettings</c>: blocks are built by the Markdown
/// renderer deep inside a transcript and cannot be handed the composition root, so the composition root replaces the two
/// readers with projections of <see cref="AppPreferences"/> before the first window opens. Tests and any host without
/// preferences keep the built-in defaults (the same values the preferences ship with).
/// </summary>
internal static class MermaidSettings
{
    private static readonly Lock gate = new();
    private static string? parsedFrom;
    private static MermaidKinds parsed;

    /// <summary>The comma-separated whitelist, exactly as stored in the <c>chat</c> section.</summary>
    internal static Func<string> DiagramTypes { get; set; } = static () => AppPreferences.DefaultChatMermaidDiagramTypes;

    /// <summary>Longest one diagram may render before it is reported as timed out.</summary>
    internal static Func<int> TimeoutMilliseconds { get; set; } = static () => AppPreferences.DefaultChatMermaidTimeoutMilliseconds;

    /// <summary>
    /// The whitelist as flags. Parsing happens once per distinct setting value, not once per diagram: this is read for every
    /// fenced block that appears while a reply streams, and the setting only changes when the user edits it.
    /// </summary>
    internal static MermaidKinds EnabledTypes()
    {
        var setting = DiagramTypes() ?? string.Empty;
        lock (gate)
        {
            if (parsedFrom is not null && string.Equals(setting, parsedFrom, StringComparison.Ordinal))
            {
                return parsed;
            }

            parsed = MermaidDiagramTypes.Parse(setting);
            parsedFrom = setting;
            return parsed;
        }
    }

    /// <summary>The configured render timeout as a <see cref="TimeSpan"/>, clamped like the preference itself.</summary>
    internal static TimeSpan Timeout()
    {
        var milliseconds = Math.Clamp(
            TimeoutMilliseconds(),
            AppPreferences.MinChatMermaidTimeoutMilliseconds,
            AppPreferences.MaxChatMermaidTimeoutMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>Restores the defaults (tests); the composition root re-binds the projections on the next start.</summary>
    internal static void Reset()
    {
        DiagramTypes = static () => AppPreferences.DefaultChatMermaidDiagramTypes;
        TimeoutMilliseconds = static () => AppPreferences.DefaultChatMermaidTimeoutMilliseconds;
        parsed = 0;
        parsedFrom = null;
    }
}
