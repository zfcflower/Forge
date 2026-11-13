using System.Text;
using System.Text.Json;
using Forge.App.Localization;

namespace Forge.App.ViewModels.Preview;

internal enum NotebookCellKind
{
    Markdown,
    Code,
    Raw,
}

internal enum NotebookOutputKind
{
    /// <summary>A <c>stream</c> output's text.</summary>
    Stream,

    /// <summary>An <c>execute_result</c> or <c>display_data</c> <c>text/plain</c> payload.</summary>
    Text,

    /// <summary>An <c>execute_result</c> or <c>display_data</c> <c>image/png</c> payload.</summary>
    Image,

    /// <summary>An <c>error</c> output.</summary>
    Error,
}

/// <summary>One parsed notebook cell, ready to become a view model.</summary>
internal sealed record NotebookCellData(NotebookCellKind Kind, string Source, IReadOnlyList<NotebookOutputData> Outputs);

internal abstract record NotebookOutputData;

internal sealed record NotebookTextOutputData(NotebookOutputKind Kind, string Text) : NotebookOutputData;

internal sealed record NotebookImageOutputData(byte[] Bytes) : NotebookOutputData;

internal sealed record NotebookErrorOutputData(string Name, string Value, string Traceback) : NotebookOutputData;

/// <summary>The parse outcome: the cells, or the reason the JSON could not be read as a notebook.</summary>
internal readonly record struct NotebookParseResult(IReadOnlyList<NotebookCellData> Cells, string? Problem)
{
    public bool Failed => Problem is not null;
}

/// <summary>
/// A tolerant <c>nbformat</c> reader: it keeps the cells and outputs it knows (markdown/code/raw cells; stream,
/// execute_result/display_data and error outputs) and skips everything else. A malformed notebook is a problem message,
/// never an exception. Every output string is truncated to <c>maxOutputChars</c> with a localized marker.
/// </summary>
internal static class NotebookPreviewReader
{
    public static NotebookParseResult Parse(string json, int maxOutputChars)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object ||
                !root.TryGetProperty("cells", out var cellsElement) ||
                cellsElement.ValueKind is not JsonValueKind.Array)
            {
                return new NotebookParseResult([], "no cells array");
            }

            var cells = new List<NotebookCellData>();
            foreach (var cellElement in cellsElement.EnumerateArray())
            {
                if (cellElement.ValueKind is JsonValueKind.Object && ParseCell(cellElement, maxOutputChars) is { } cell)
                {
                    cells.Add(cell);
                }
            }

            return new NotebookParseResult(cells, null);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return new NotebookParseResult([], exception.Message);
        }
    }

    private static NotebookCellData? ParseCell(JsonElement cell, int maxOutputChars)
    {
        if (!cell.TryGetProperty("cell_type", out var typeElement) || typeElement.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        NotebookCellKind? kind = typeElement.GetString() switch
        {
            "markdown" => NotebookCellKind.Markdown,
            "code" => NotebookCellKind.Code,
            "raw" => NotebookCellKind.Raw,
            _ => null,
        };
        if (kind is null)
        {
            return null;
        }

        var source = cell.TryGetProperty("source", out var sourceElement)
            ? ReadStringOrArray(sourceElement) ?? string.Empty
            : string.Empty;

        var outputs = new List<NotebookOutputData>();
        if (cell.TryGetProperty("outputs", out var outputsElement) && outputsElement.ValueKind is JsonValueKind.Array)
        {
            foreach (var output in outputsElement.EnumerateArray())
            {
                if (output.ValueKind is JsonValueKind.Object)
                {
                    ParseOutput(output, maxOutputChars, outputs);
                }
            }
        }

        return new NotebookCellData(kind.Value, source, outputs);
    }

    private static void ParseOutput(JsonElement output, int maxOutputChars, List<NotebookOutputData> outputs)
    {
        if (!output.TryGetProperty("output_type", out var typeElement) || typeElement.ValueKind is not JsonValueKind.String)
        {
            return;
        }

        switch (typeElement.GetString())
        {
            case "stream":
                if (output.TryGetProperty("text", out var textElement) &&
                    ReadStringOrArray(textElement) is { Length: > 0 } text)
                {
                    outputs.Add(new NotebookTextOutputData(NotebookOutputKind.Stream, Truncate(text, maxOutputChars)));
                }

                break;

            case "execute_result":
            case "display_data":
                ParseDataOutput(output, maxOutputChars, outputs);
                break;

            case "error":
            {
                var name = output.TryGetProperty("ename", out var nameElement) ? ReadStringOrArray(nameElement) ?? string.Empty : string.Empty;
                var value = output.TryGetProperty("evalue", out var valueElement) ? ReadStringOrArray(valueElement) ?? string.Empty : string.Empty;
                var traceback = output.TryGetProperty("traceback", out var tracebackElement)
                    ? ReadStringOrArray(tracebackElement) ?? string.Empty
                    : string.Empty;
                outputs.Add(new NotebookErrorOutputData(
                    Truncate(name, maxOutputChars),
                    Truncate(value, maxOutputChars),
                    Truncate(traceback, maxOutputChars)));
                break;
            }
        }
    }

    private static void ParseDataOutput(JsonElement output, int maxOutputChars, List<NotebookOutputData> outputs)
    {
        if (!output.TryGetProperty("data", out var dataElement) || dataElement.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        if (dataElement.TryGetProperty("text/plain", out var textElement) &&
            ReadStringOrArray(textElement) is { Length: > 0 } text)
        {
            outputs.Add(new NotebookTextOutputData(NotebookOutputKind.Text, Truncate(text, maxOutputChars)));
        }

        if (dataElement.TryGetProperty("image/png", out var pngElement) &&
            pngElement.ValueKind is JsonValueKind.String &&
            pngElement.GetString() is { Length: > 0 } base64)
        {
            try
            {
                outputs.Add(new NotebookImageOutputData(Convert.FromBase64String(base64)));
            }
            catch (FormatException)
            {
                // A corrupted PNG payload is skipped; the rest of the notebook still renders.
            }
        }
    }

    /// <summary>A <c>source</c>/<c>text</c>/<c>traceback</c> value: a plain string, or an array of strings joined without separators.</summary>
    private static string? ReadStringOrArray(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.Array:
            {
                var builder = new StringBuilder();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.String)
                    {
                        builder.Append(item.GetString());
                    }
                }

                return builder.ToString();
            }

            default:
                return null;
        }
    }

    /// <summary>The localized truncation marker is resolved at parse time: a preview is transient content.</summary>
    private static string Truncate(string text, int maxOutputChars) =>
        text.Length <= maxOutputChars ? text : text[..maxOutputChars] + PreviewStrings.TruncatedMarker;
}
