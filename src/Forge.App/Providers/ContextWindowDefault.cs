using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Forge.App.Providers;

/// <summary>
/// The global <c>modelCatalog.defaultContextWindow</c> setting: either an absolute token count or a share of each model's
/// own window. One property in the file carries both meanings, so the parse, the stored shape and the arithmetic live here
/// instead of being repeated by the store, the catalogue and the settings page.
/// </summary>
/// <remarks>
/// <para>
/// A share only ever scales a window somebody else already knows — the catalog snapshot, what the provider reported, or a
/// per-model override. A model no layer knows a window for stays unknown: inventing one would make the composer show a
/// percentage of a guess. A share above 100% is accepted, because a provider may allow a larger window than the one it
/// advertises and the user's arithmetic is not this type's business; only 1%–<see cref="MaxPercent"/> is a share at all.
/// </para>
/// <para>
/// A count is stored as a JSON number and a share as a string such as <c>"50%"</c>, so a build that predates shares reads a
/// count exactly as before and treats a share it cannot parse as "no global default" rather than as a wrong number.
/// </para>
/// </remarks>
internal readonly struct ContextWindowDefault
{
    /// <summary>The largest share accepted, so a slipped keypress such as <c>1000000%</c> is refused instead of written.</summary>
    public const int MaxPercent = 1000;

    private readonly int tokens;
    private readonly int percent;

    private ContextWindowDefault(int tokens, int percent)
    {
        this.tokens = tokens;
        this.percent = percent;
    }

    /// <summary>Nothing set: the property is absent and every model keeps the window its own layers report.</summary>
    public static ContextWindowDefault Empty => default;

    /// <summary>An absolute count; a value that is not positive is <see cref="Empty"/> rather than a zero-token model.</summary>
    public static ContextWindowDefault FromTokens(int value) => value > 0 ? new ContextWindowDefault(value, 0) : Empty;

    /// <summary>A share of each model's window; a share outside 1..<see cref="MaxPercent"/> is <see cref="Empty"/>.</summary>
    public static ContextWindowDefault FromPercent(int value) =>
        value is >= 1 and <= MaxPercent ? new ContextWindowDefault(0, value) : Empty;

    /// <summary>True when the user cleared the setting, which is what removes the property from the file.</summary>
    public bool IsEmpty => tokens == 0 && percent == 0;

    /// <summary>The absolute count, or <c>null</c> when the setting is empty or a share.</summary>
    public int? Tokens => tokens > 0 ? tokens : null;

    /// <summary>The share in percent, or <c>null</c> when the setting is empty or an absolute count.</summary>
    public int? Percent => percent > 0 ? percent : null;

    /// <summary>
    /// Reads the settings input: empty is <see cref="Empty"/>, a positive whole number is an absolute count, and
    /// <c>"50%"</c> (or <c>"50 %"</c>) is a share. Everything else is refused so the caller can report it and write
    /// nothing: a sign, a fraction, a thousands separator, a bare <c>%</c>, <c>0%</c>, or a share above
    /// <see cref="MaxPercent"/>.
    /// </summary>
    public static bool TryParse(string? text, out ContextWindowDefault value)
    {
        value = Empty;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.EndsWith('%'))
        {
            // "50 %" is the same share as "50%": the space before the sign is a typo, not a second meaning.
            var share = trimmed[..^1].TrimEnd();
            if (!TryReadCount(share, out var parsedShare) || parsedShare is < 1 or > MaxPercent)
            {
                return false;
            }

            value = FromPercent(parsedShare);
            return true;
        }

        if (!TryReadCount(trimmed, out var parsedCount) || parsedCount < 1)
        {
            return false;
        }

        value = FromTokens(parsedCount);
        return true;
    }

    /// <summary>
    /// Reads the stored property of one <c>settings.json</c>: a number is an absolute count, a string is either a count or
    /// a share. A value this build cannot make sense of (a fraction, a negative, a null, a damaged entry) reads as
    /// <see cref="Empty"/> instead of throwing — the file is configuration, so it degrades to "no global default" and the
    /// page shows the empty box rather than refusing to start.
    /// </summary>
    public static ContextWindowDefault FromJson(JsonElement? stored)
    {
        if (stored is not { } element)
        {
            return Empty;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out var count) ? FromTokens(count) : Empty,
            JsonValueKind.String => TryParse(element.GetString(), out var parsed) ? parsed : Empty,
            _ => Empty,
        };
    }

    /// <summary>
    /// The value the file holds: a number for a count, a string for a share, and <c>null</c> for an empty setting (the
    /// store removes the property rather than writing an empty value).
    /// </summary>
    public JsonNode? ToNode() => percent > 0
        ? JsonValue.Create(percent.ToString(CultureInfo.InvariantCulture) + "%")
        : tokens > 0
            ? JsonValue.Create(tokens)
            : null;

    /// <summary>
    /// The window this setting produces for a model whose own window is <paramref name="modelWindow"/> — whatever the
    /// chain of catalog, provider report and per-model override already found. An absolute count answers itself; a share
    /// answers <c>floor(window × share / 100)</c> clamped to at least one token; an empty setting answers the window it was
    /// given, unchanged. A share with no known window answers <c>null</c>: the model stays unknown.
    /// </summary>
    public int? Resolve(int? modelWindow)
    {
        if (tokens > 0)
        {
            return tokens;
        }

        if (percent == 0)
        {
            return modelWindow;
        }

        if (modelWindow is not { } window || window <= 0)
        {
            return null;
        }

        var scaled = (long)window * percent / 100;
        return scaled < 1 ? 1 : (int)Math.Min(scaled, int.MaxValue);
    }

    /// <summary>The text the settings input shows: the count, the share with its percent sign, or empty.</summary>
    public override string ToString() => percent > 0
        ? percent.ToString(CultureInfo.InvariantCulture) + "%"
        : tokens > 0
            ? tokens.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

    /// <summary>
    /// A plain integer is a count, which is how the setting was stored before shares existed and how most callers still
    /// name one. <c>null</c> and a non-positive number clear it.
    /// </summary>
    public static implicit operator ContextWindowDefault(int? count) => count is { } value ? FromTokens(value) : Empty;

    private static bool TryReadCount(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
