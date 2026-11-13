using Forge.App.Localization;
using Forge.App.Services;

namespace Forge.App.ViewModels;

/// <summary>
/// The preview half of the palette: what sits beside the list. A command, a session or a notice explains itself from the
/// row's own fields; a file-backed row (a file, a matching line, a symbol) is read through <see cref="PalettePreview"/>,
/// which bounds the bytes, decodes on the thread pool and keeps the last file so moving between the rows of one file does
/// no I/O at all.
/// </summary>
internal sealed partial class PaletteViewModel
{
    /// <summary>Reloads the preview for the current selection, cancelling the read of the previous one.</summary>
    private void LoadPreview()
    {
        Cancel(ref previewCts);
        if (selectedItem is not { } item)
        {
            IsPreviewLoading = false;
            SetPreview(string.Empty, string.Empty);
            return;
        }

        var title = PreviewTitleFor(item);
        if (item.Kind is PaletteItemKind.Command or PaletteItemKind.Session or PaletteItemKind.Notice)
        {
            IsPreviewLoading = false;
            SetPreview(title, ImpliedPreview(item));
            return;
        }

        IsPreviewLoading = true;
        SetPreview(title, string.Empty);
        var cts = new CancellationTokenSource();
        previewCts = cts;
        _ = LoadFilePreviewAsync(item, title, cts);
    }

    private async Task LoadFilePreviewAsync(PaletteItem item, string title, CancellationTokenSource cts)
    {
        string? text = null;
        try
        {
            if (ResolveAbsolute(item.Path) is { } absolute)
            {
                text = await preview.ReadAsync(absolute, item.Line, cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(previewCts, cts))
            {
                previewCts = null;
                IsPreviewLoading = false;
            }

            cts.Dispose();
        }

        SetPreview(title, string.IsNullOrEmpty(text) ? Loc.Instance[PaletteStrings.PreviewEmpty] : text);
    }

    /// <summary>The heading of the preview pane: the kind of row plus what it points at.</summary>
    private static string PreviewTitleFor(PaletteItem item) => item.Kind switch
    {
        PaletteItemKind.ContentLine => Loc.Instance[PaletteStrings.PreviewContent] + " · " +
            Loc.Instance.Format(PaletteStrings.PreviewAroundLine, TextFormat.Number(item.Line)),
        PaletteItemKind.Session => Loc.Instance[PaletteStrings.PreviewSession],
        PaletteItemKind.Command => Loc.Instance[PaletteStrings.PreviewCommand],
        PaletteItemKind.Symbol => Loc.Instance[PaletteStrings.PreviewSymbol] + " · " + item.Title,
        PaletteItemKind.Notice => Loc.Instance[PaletteStrings.PreviewNotice],
        _ => Loc.Instance[PaletteStrings.PreviewFile],
    };

    /// <summary>The preview of a row that needs no file: its own fields already say everything there is to say.</summary>
    private static string ImpliedPreview(PaletteItem item)
    {
        var gesture = item.Gesture.Length == 0 ? string.Empty : item.Gesture;
        return item.Kind switch
        {
            PaletteItemKind.Command => Join(item.Detail, gesture),
            PaletteItemKind.Session => Join(item.Title, item.Detail, item.Badge),
            _ => item.Title,
        };
    }

    private void SetPreview(string title, string text)
    {
        PreviewTitle = title;
        PreviewText = text;
    }

    /// <summary>The absolute path a row points at: as typed when it is already absolute, otherwise below the palette's workspace.</summary>
    private string? ResolveAbsolute(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (Path.IsPathFullyQualified(path))
        {
            return path;
        }

        return string.IsNullOrEmpty(host.Workspace) ? null : Path.GetFullPath(Path.Combine(host.Workspace, path));
    }

    private static string Join(params ReadOnlySpan<string?> parts)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(part);
        }

        return builder.ToString();
    }
}
