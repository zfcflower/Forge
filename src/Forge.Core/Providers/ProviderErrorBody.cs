using System.Text;
using System.Text.Json;

namespace Forge.Core.Providers;

/// <summary>
/// Turns the body of a rejected model request into the one sentence a user can act on. Providers wrap the same fact in a few
/// envelopes — OpenAI and Anthropic both nest it under <c>error.message</c>, a gateway sometimes adds <c>error.param</c> or a
/// replacement model on a <c>410</c>, and a bare server may send nothing but text — so the sentence is dug out rather than
/// pasted, while the body itself is kept on the exception (<see cref="ProviderErrors.ErrorBodyKey"/>): a phrase rule still has
/// to read the whole thing, and support may need the rest.
/// </summary>
internal static class ProviderErrorBody
{
    /// <summary>
    /// The sentence the body carries, plus the hints worth showing. A body that is not the expected JSON — an HTML error page,
    /// one plain line — is passed through with its line breaks folded, so a footnote stays one line.
    /// </summary>
    internal static string Describe(string? body)
    {
        var raw = (body ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return "(empty body)";
        }

        string? message = null;
        string? param = null;
        string? kind = null;
        string? replacement = null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.Object:
                    root.TryGetProperty("error", out var error);
                    message = Sentence(root, "message")
                        ?? Sentence(error, "message")
                        ?? (error.ValueKind == JsonValueKind.String ? Sentence(error.GetString()) : null);
                    param = Sentence(error, "param") ?? Sentence(root, "param");
                    kind = Sentence(error, "type") ?? Sentence(root, "type");
                    if (root.TryGetProperty("metadata", out var metadata))
                    {
                        replacement = Sentence(metadata, "replacement");
                    }

                    break;

                case JsonValueKind.String:
                    message = Sentence(root.GetString());
                    break;
            }
        }
        catch (JsonException)
        {
            // Not JSON: the folded raw text is the best sentence available, which is what a proxy's HTML page looks like.
        }

        var text = new StringBuilder(message ?? Fold(raw));
        Append(text, "param", param);

        // "invalid_request_error" is the generic type every one of these bodies carries, and a bare "error" is only the envelope's
        // own name; only a specific type earns the space.
        if (kind is { Length: > 0 } && !string.Equals(kind, "invalid_request_error", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(kind, "error", StringComparison.OrdinalIgnoreCase))
        {
            Append(text, "type", kind);
        }

        Append(text, "use", replacement);
        return text.ToString();
    }

    private static string? Sentence(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? Sentence(value.GetString()) : null
            : null;

    private static string? Sentence(string? value)
    {
        var folded = Fold(value ?? string.Empty);
        return folded.Length == 0 ? null : folded;
    }

    /// <summary>One line: a message carrying its own newlines would break the turn divider's layout.</summary>
    private static string Fold(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string label, string? value)
    {
        if (value is not { Length: > 0 } text)
        {
            return;
        }

        builder.Append(" (").Append(label).Append(": ").Append(text).Append(')');
    }
}
