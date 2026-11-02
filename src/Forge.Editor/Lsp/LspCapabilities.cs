using System.Text.Json;
using Forge.Editor.Text;

namespace Forge.Editor.Lsp;

public sealed record SemanticTokensCapability(SemanticTokensLegend Legend, bool Full, bool FullDelta, bool Range)
{
    public bool SupportsDelta => Full && FullDelta;
}

/// <summary>Server capabilities the editor acts on, normalised from the <c>initialize</c> result.</summary>
public sealed record LspServerCapabilities(
    bool SupportsIncrementalSync,
    bool SupportsFullSync,
    bool SupportsOpenClose,
    bool SupportsSave,
    bool SupportsDocumentSymbols,
    bool SupportsHover,
    bool SupportsCompletion,
    bool SupportsDefinition,
    bool SupportsReferences,
    bool SupportsWorkspaceFolders,
    PositionEncoding PositionEncoding,
    SemanticTokensCapability? SemanticTokens,
    JsonElement Raw)
{
    public static LspServerCapabilities Parse(JsonElement result, PositionEncoding preferred)
    {
        var capabilities = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("capabilities", out var caps) ? caps : default;
        var syncKind = 0;
        var openClose = false;
        var save = false;
        if (capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty("textDocumentSync", out var sync))
        {
            if (sync.ValueKind == JsonValueKind.Number)
            {
                syncKind = sync.GetInt32();
                openClose = syncKind > 0;
            }
            else if (sync.ValueKind == JsonValueKind.Object)
            {
                if (sync.TryGetProperty("openClose", out var oc) && oc.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    openClose = oc.GetBoolean();
                }

                if (sync.TryGetProperty("change", out var change) && change.ValueKind == JsonValueKind.Number)
                {
                    syncKind = change.GetInt32();
                }

                if (sync.TryGetProperty("save", out var saveValue))
                {
                    save = saveValue.ValueKind is JsonValueKind.True or JsonValueKind.Object;
                }
            }
        }

        var encoding = preferred;
        if (capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty("positionEncoding", out var positionEncoding) &&
            positionEncoding.ValueKind == JsonValueKind.String)
        {
            encoding = positionEncoding.GetString() switch
            {
                "utf-8" => PositionEncoding.Utf8,
                "utf-32" => PositionEncoding.Utf8,
                _ => PositionEncoding.Utf16,
            };
        }

        return new LspServerCapabilities(
            SupportsIncrementalSync: syncKind == 2,
            SupportsFullSync: syncKind == 1,
            SupportsOpenClose: openClose,
            SupportsSave: save,
            SupportsDocumentSymbols: Has(capabilities, "documentSymbolProvider"),
            SupportsHover: Has(capabilities, "hoverProvider"),
            SupportsCompletion: Has(capabilities, "completionProvider"),
            SupportsDefinition: Has(capabilities, "definitionProvider"),
            SupportsReferences: Has(capabilities, "referencesProvider"),
            SupportsWorkspaceFolders: Has(capabilities, "workspace") &&
                capabilities.TryGetProperty("workspace", out var workspace) &&
                workspace.TryGetProperty("workspaceFolders", out var folders) &&
                folders.ValueKind == JsonValueKind.Object &&
                folders.TryGetProperty("supported", out var supported) &&
                supported.ValueKind == JsonValueKind.True,
            PositionEncoding: encoding,
            SemanticTokens: ParseSemanticTokens(capabilities),
            Raw: result.Clone());
    }

    private static bool Has(JsonElement capabilities, string name) =>
        capabilities.ValueKind == JsonValueKind.Object &&
        capabilities.TryGetProperty(name, out var value) &&
        value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined);

    private static SemanticTokensCapability? ParseSemanticTokens(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty("semanticTokensProvider", out var provider) ||
            provider.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var legend = SemanticTokensLegend.Empty;
        if (provider.TryGetProperty("legend", out var legendElement) && legendElement.ValueKind == JsonValueKind.Object)
        {
            var types = ReadStrings(legendElement, "tokenTypes");
            var modifiers = ReadStrings(legendElement, "tokenModifiers");
            legend = new SemanticTokensLegend(types, modifiers);
        }

        var full = false;
        var delta = false;
        if (provider.TryGetProperty("full", out var fullElement))
        {
            full = fullElement.ValueKind is JsonValueKind.True or JsonValueKind.Object;
            delta = fullElement.ValueKind == JsonValueKind.Object &&
                fullElement.TryGetProperty("delta", out var deltaValue) &&
                deltaValue.ValueKind == JsonValueKind.True;
        }

        var range = provider.TryGetProperty("range", out var rangeElement) && rangeElement.ValueKind is JsonValueKind.True or JsonValueKind.Object;
        return new SemanticTokensCapability(legend, full, delta, range);
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(item.GetString() ?? string.Empty);
            }
        }

        return result;
    }
}

/// <summary>
/// Decodes the LSP relative integer encoding of <c>semanticTokens</c> into absolute tokens and applies deltas.
/// </summary>
public static class SemanticTokensDecoder
{
    /// <summary>Number of integers per token in the wire format (deltaLine, deltaStartChar, length, type, modifiers).</summary>
    public const int FieldsPerToken = 5;

    public static IReadOnlyList<SemanticToken> Decode(ReadOnlySpan<int> data, SemanticTokensLegend legend)
    {
        if (data.Length < FieldsPerToken)
        {
            return [];
        }

        var count = data.Length / FieldsPerToken;
        var tokens = new SemanticToken[count];
        var line = 0;
        var startChar = 0;
        for (var i = 0; i < count; i++)
        {
            var offset = i * FieldsPerToken;
            var deltaLine = data[offset];
            var deltaStart = data[offset + 1];
            if (deltaLine == 0)
            {
                startChar += deltaStart;
            }
            else
            {
                line += deltaLine;
                startChar = deltaStart;
            }

            var type = data[offset + 3];
            tokens[i] = new SemanticToken(line, startChar, data[offset + 2], type, data[offset + 4], legend.TypeName(type));
        }

        return tokens;
    }

    /// <summary>
    /// Applies <c>SemanticTokensDelta.edits</c> to the previously received data array. Each edit replaces
    /// <c>DeleteCount</c> integers starting at <c>Start</c> with <paramref name="editData"/>.
    /// </summary>
    public static int[] ApplyEdits(ReadOnlySpan<int> previous, IReadOnlyList<SemanticTokensEdit> edits)
    {
        if (edits.Count == 0)
        {
            return [.. previous];
        }

        var result = new List<int>(previous.Length + 32);
        result.AddRange(previous.ToArray());
        var prefix = new List<int>(result.Capacity);
        var applied = 0;
        var consumed = 0;
        var buffer = new List<int>();
        foreach (var edit in edits)
        {
            var start = Math.Clamp(edit.Start, 0, previous.Length);
            _ = start;
            break;
        }

        // apply edits against the original array positions, in ascending start order
        var ordered = edits.OrderBy(static e => e.Start).ToList();
        var position = 0;
        foreach (var edit in ordered)
        {
            var start = Math.Clamp(edit.Start, position, previous.Length);
            if (start > position)
            {
                buffer.AddRange(previous[position..start].ToArray());
            }

            if (edit.Data is { Length: > 0 })
            {
                buffer.AddRange(edit.Data);
            }

            position = Math.Min(previous.Length, start + Math.Max(0, edit.DeleteCount));
        }

        if (position < previous.Length)
        {
            buffer.AddRange(previous[position..].ToArray());
        }

        _ = prefix;
        _ = applied;
        _ = consumed;
        return [.. buffer];
    }

    /// <summary>Reads the <c>data</c> array of a <c>semanticTokens</c> result.</summary>
    public static int[] ReadData(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return ReadIntArray(data);
    }

    /// <summary>Reads a JSON array of integers into a new array.</summary>
    public static int[] ReadIntArray(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new int[array.GetArrayLength()];
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            result[index++] = item.GetInt32();
        }

        return result;
    }
}

public readonly record struct SemanticTokensEdit(int Start, int DeleteCount, int[]? Data);
