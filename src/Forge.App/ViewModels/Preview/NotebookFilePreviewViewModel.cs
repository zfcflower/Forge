using Avalonia.Media.Imaging;
using Forge.App.Localization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.ViewModels.Preview;

/// <summary>
/// An <c>.ipynb</c> preview: cells and outputs parsed off the UI thread. Code sources render in a monospace
/// selectable block; <c>image/png</c> outputs decode lazily through the same injectable decoder as the image preview
/// (and render nothing when it returns null). A malformed notebook is a notice, never an exception.
/// </summary>
internal sealed class NotebookFilePreviewViewModel : FilePreviewViewModel
{
    private NotebookFilePreviewViewModel(string path, string name, long byteSize, IReadOnlyList<NotebookCellViewModel> cells)
        : base(path, name, byteSize)
    {
        Cells = cells;
    }

    public IReadOnlyList<NotebookCellViewModel> Cells { get; }

    /// <summary>Largest <c>.ipynb</c> the preview reads, matching the text document's own bound.</summary>
    public const long MaxNotebookBytes = 8L * 1024 * 1024;

    public override string Caption => PreviewStrings.NotebookCaption(Name, Cells.Count).Resolve();

    public override void RefreshText()
    {
        base.RefreshText();
        foreach (var cell in Cells)
        {
            cell.RefreshText();
        }
    }

    protected override void ReleaseCore()
    {
        foreach (var cell in Cells)
        {
            cell.Release();
        }
    }

    public static NotebookFilePreviewViewModel Build(
        string path,
        long byteSize,
        int maxOutputChars,
        Func<byte[], Bitmap?>? decoder,
        CancellationToken ct = default)
    {
        var name = System.IO.Path.GetFileName(path);
        if (byteSize > MaxNotebookBytes)
        {
            // Read as text, so it is bounded like the editor document: a notebook carrying screenshots reaches hundreds of
            // megabytes and must not be pulled into memory just to be drawn.
            var tooLarge = new NotebookFilePreviewViewModel(path, name, byteSize, []);
            tooLarge.SetNotice(PreviewStrings.NotebookTooLarge(name, MaxNotebookBytes / (1024 * 1024)));
            return tooLarge;
        }

        try
        {
            var json = File.ReadAllText(path);
            ct.ThrowIfCancellationRequested();
            var parsed = NotebookPreviewReader.Parse(json, maxOutputChars);
            if (parsed.Problem is not null)
            {
                var failed = new NotebookFilePreviewViewModel(path, name, byteSize, []);
                failed.SetNotice(PreviewStrings.NotebookParseFailed(name, parsed.Problem));
                return failed;
            }

            var cells = new NotebookCellViewModel[parsed.Cells.Count];
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i] = NotebookCellViewModel.Create(parsed.Cells[i], decoder);
            }

            return new NotebookFilePreviewViewModel(path, name, byteSize, cells);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            var viewModel = new NotebookFilePreviewViewModel(path, name, byteSize, []);
            viewModel.SetNotice(PreviewStrings.NotebookReadFailed(name, exception.Message));
            return viewModel;
        }
    }
}

/// <summary>One notebook cell: its kind, source and outputs; the source is never truncated.</summary>
internal sealed class NotebookCellViewModel : ObservableObject
{
    private NotebookCellViewModel(NotebookCellKind kind, string source, IReadOnlyList<NotebookOutputViewModel> outputs)
    {
        Kind = kind;
        Source = source;
        Outputs = outputs;
    }

    public NotebookCellKind Kind { get; }

    public string Source { get; }

    public bool HasSource => Source.Length > 0;

    public IReadOnlyList<NotebookOutputViewModel> Outputs { get; }

    public bool IsCode => Kind == NotebookCellKind.Code;

    public bool IsMarkdown => Kind == NotebookCellKind.Markdown;

    public bool IsRaw => Kind == NotebookCellKind.Raw;

    /// <summary>The kind's label ("代码" / "Code"), re-read on every access so a language switch refreshes it.</summary>
    public string KindLabel => PreviewStrings.NotebookCellKindLabel(Kind);

    internal void RefreshText()
    {
        OnPropertyChanged(nameof(KindLabel));
        foreach (var output in Outputs)
        {
            output.RefreshText();
        }
    }

    internal void Release()
    {
        foreach (var output in Outputs)
        {
            output.Release();
        }
    }

    internal static NotebookCellViewModel Create(NotebookCellData data, Func<byte[], Bitmap?>? decoder)
    {
        var outputs = new NotebookOutputViewModel[data.Outputs.Count];
        for (var i = 0; i < outputs.Length; i++)
        {
            outputs[i] = NotebookOutputViewModel.Create(data.Outputs[i], decoder);
        }

        return new NotebookCellViewModel(data.Kind, data.Source, outputs);
    }
}

/// <summary>One notebook output: a stream/result text, a lazily decoded PNG, or an error with its traceback.</summary>
internal sealed class NotebookOutputViewModel : ObservableObject
{
    private readonly byte[]? imageBytes;
    private readonly Func<byte[], Bitmap?>? decoder;
    private Bitmap? image;
    private bool decodeStarted;
    private bool released;

    private NotebookOutputViewModel(
        NotebookOutputKind kind,
        string? text,
        byte[]? imageBytes,
        string? errorName,
        string? errorValue,
        string? traceback,
        Func<byte[], Bitmap?>? decoder)
    {
        Kind = kind;
        Text = text;
        this.imageBytes = imageBytes;
        ErrorName = errorName;
        ErrorValue = errorValue;
        Traceback = traceback;
        ErrorSummary = errorValue is null ? string.Empty : errorName is { Length: > 0 } name ? name + ": " + errorValue : errorValue;
        this.decoder = decoder;
    }

    public NotebookOutputKind Kind { get; }

    /// <summary>The (already truncated) text of a stream or result output; null for images and errors.</summary>
    public string? Text { get; }

    public bool HasText => Text is not null;

    public string? ErrorName { get; }

    public string? ErrorValue { get; }

    public string? Traceback { get; }

    /// <summary>"ename: evalue", the one line the view shows in the danger color.</summary>
    public string ErrorSummary { get; }

    public bool HasError => ErrorValue is not null;

    public bool HasImage => imageBytes is not null;

    /// <summary>The output's kind label ("输出" / "Output"), re-read on every access.</summary>
    public string KindLabel => PreviewStrings.NotebookOutputKindLabel(Kind);

    /// <summary>
    /// The decoded PNG, decoded lazily off the UI thread the first time a view asks for it (by the image preview's bounded
    /// decoder unless one was injected); null while decoding, when the decoder fails, or when a test's stub returns null
    /// (then nothing renders).
    /// </summary>
    public Bitmap? Image
    {
        get
        {
            if (image is null && !decodeStarted && imageBytes is { } bytes)
            {
                decodeStarted = true;
                _ = DecodeAsync(bytes);
            }

            return image;
        }
    }

    internal void RefreshText() => OnPropertyChanged(nameof(KindLabel));

    internal void Release()
    {
        released = true;
        image?.Dispose();
        image = null;
    }

    internal static NotebookOutputViewModel Create(NotebookOutputData data, Func<byte[], Bitmap?>? decoder) => data switch
    {
        NotebookTextOutputData text => new NotebookOutputViewModel(text.Kind, text.Text, null, null, null, null, decoder),
        NotebookImageOutputData image => new NotebookOutputViewModel(NotebookOutputKind.Image, null, image.Bytes, null, null, null, decoder),
        NotebookErrorOutputData error => new NotebookOutputViewModel(NotebookOutputKind.Error, null, null, error.Name, error.Value, error.Traceback, decoder),
        _ => throw new ArgumentOutOfRangeException(nameof(data), "unknown notebook output kind"),
    };

    private async Task DecodeAsync(byte[] bytes)
    {
        // No injected decoder is the production path: the same bounded decoder as the image preview.
        var codec = decoder ?? ImageFilePreviewViewModel.DefaultDecoder;
        Bitmap? decoded;
        try
        {
            decoded = await Task.Run(() =>
            {
                try
                {
                    return codec(bytes);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    AppLogs.File.ZLogWarning($"notebook image decode failed: {exception.Message}");
                    return null;
                }
            }).ConfigureAwait(true);
        }
        catch
        {
            decoded = null;
        }

        if (released)
        {
            decoded?.Dispose();
            return;
        }

        image = decoded;
        OnPropertyChanged(nameof(Image));
    }
}
