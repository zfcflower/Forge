using System.Globalization;
using System.Text.Json;

namespace Forge.App.Providers;

/// <summary>
/// The fields a provider sign-in answer carries: the non-empty strings, and the numbers a device-code answer uses to say
/// how long the code lives and how long to wait before the next poll. Every sign-in reads them the same way and
/// defensively, because a misread interval either polls the server too often or waits past the code's lifetime.
/// </summary>
internal static class ProviderSignInJson
{
    /// <summary>The property's text when it is a non-empty JSON string, otherwise <c>null</c>.</summary>
    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>A positive whole number of seconds, from a JSON number that may still arrive as a fraction.</summary>
    public static int? PositiveInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number)
            || number <= 0)
        {
            return null;
        }

        // Rounded up: waiting a fraction of a second too long is harmless, polling early is not.
        return (int)Math.Min(Math.Ceiling(number), int.MaxValue);
    }

    /// <summary>The interval to wait, never below <paramref name="floor"/>.</summary>
    public static int Interval(JsonElement element, int floor) =>
        PositiveInt(element, "interval") is { } named ? Math.Max(floor, named) : floor;
}
