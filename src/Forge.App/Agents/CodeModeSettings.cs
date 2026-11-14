using Forge.App.Preferences;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.App.Agents;

/// <summary>
/// The <c>tools</c> section's four script dials (wave W14), snapshotted: how many inner tool calls of one script may be in
/// flight, how much of a single inner result and of the returned result the main program keeps, and which of the callable
/// tools the script may see.
/// <para>
/// The dials are read from <see cref="AppPreferences"/> on every use instead of being captured once, so a settings edit
/// reaches the next model request's <c>run_code</c> description and every later script without rebuilding a session host.
/// A read costs one small record and, for a non-blank list, one split of a short string; it happens once per description
/// build and once per inner tool call, each of which already pays an IPC round trip and a JSON parse.
/// </para>
/// <para>
/// The list can only ever <b>narrow</b>: which tool families scripts may call at all is decided in code
/// (<see cref="CodeModeToolRegistry"/>), because widening that set would route a write or execute tool past the
/// per-call approval card the model's own calls get. A blank list therefore means "every candidate", never "add one".
/// </para>
/// </summary>
internal sealed record CodeModeSettings(
    int MaxConcurrentCalls,
    int ResultChars,
    int InnerResultChars,
    IReadOnlyList<string> VisibleTools)
{
    /// <summary>The SDK's own defaults, the single source of the three numbers <see cref="Default"/> carries: a script
    /// with no settings store must be bounded exactly like one that never touched the fields.</summary>
    private static readonly Sdk.CodeExecutionOptions Defaults = new();

    /// <summary>The values in force when the runtime was assembled without the settings store (a unit test).</summary>
    public static CodeModeSettings Default { get; } = new(
        Defaults.MaxConcurrentToolCalls,
        Defaults.MaxResultChars,
        Defaults.MaxInnerResultChars,
        []);

    /// <summary>The characters that separate two names of the visible-tool list.</summary>
    private static readonly char[] Separators = [',', ';', '\n', '\r', '\t', ' '];

    /// <summary>Reads the four dials of the <c>tools</c> section. The setters clamp, so this reads ranges that are already valid.</summary>
    public static CodeModeSettings From(AppPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return new CodeModeSettings(
            preferences.CodeScriptMaxConcurrentCalls,
            preferences.CodeScriptResultChars,
            preferences.CodeScriptInnerResultChars,
            ParseTools(preferences.CodeScriptTools));
    }

    /// <summary>
    /// The execution bounds to hand to the <c>run_code</c> proxy. Only the three script dials are overridden: the script
    /// size, log and diagnostic bounds keep <see cref="Sdk.CodeExecutionOptions"/>' defaults.
    /// </summary>
    public Sdk.CodeExecutionOptions ToExecution() => new()
    {
        MaxConcurrentToolCalls = MaxConcurrentCalls,
        MaxResultChars = ResultChars,
        MaxInnerResultChars = InnerResultChars,
    };

    /// <summary>
    /// Whether a callable tool is visible under this list: an empty list admits everything it is offered, a name admits
    /// that tool, and a name ending in <c>*</c> admits the tools whose name starts with the part before it (that is how a
    /// whole MCP server, <c>github__*</c>, is admitted). Matching ignores case and never matches part of a name without
    /// the wildcard.
    /// </summary>
    public bool Allows(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        var visible = VisibleTools;
        if (visible.Count == 0)
        {
            return true;
        }

        foreach (var pattern in visible)
        {
            if (pattern.EndsWith('*'))
            {
                if (toolName.AsSpan().StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(pattern, toolName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The list as names: separators collapsed, blanks dropped, case-insensitive duplicates removed. A blank
    /// text is the empty list, which admits every candidate.</summary>
    internal static IReadOnlyList<string> ParseTools(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var parts = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(parts.Length, StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (seen.Add(part))
            {
                names.Add(part);
            }
        }

        return names;
    }
}
