using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using Forge.App.Localization;
using Forge.Editor.Folding;
using Forge.Editor.Text;
using EditorDocument = AvaloniaEdit.Document.TextDocument;
using ForgeSnapshot = Forge.Editor.Text.TextSnapshot;

namespace Forge.App.Editing;

/// <summary>
/// Folding for one open document: the library's <see cref="FoldingManager"/> and margin, plus where the ranges come from.
/// <para>The language server wins when it can fold (<c>foldingRangeProvider</c>); otherwise the structural heuristic runs
/// over the Forge snapshot, off the UI thread, and XML-shaped files use the library's own XML strategy. Recomputing waits
/// for a pause in typing, and a result computed for an older document version is dropped instead of folding the wrong
/// lines.</para>
/// <para>Fold state belongs to the control: it is not persisted and does not survive reopening the file.</para>
/// </summary>
internal sealed class EditorFoldingController : IDisposable
{
    private const int DebounceMilliseconds = 250;

    private readonly TextEditor editor;
    private readonly EditorDocument document;
    private readonly Func<ForgeSnapshot> snapshot;
    private readonly Func<CancellationToken, Task<IReadOnlyList<FoldingRange>?>> serverRanges;
    private readonly Action<LocText> notice;
    private readonly FoldingSyntax syntax;
    private readonly bool xml;
    private readonly DispatcherTimer timer;

    private FoldingManager? manager;
    private CancellationTokenSource? pending;
    private LocText reportedNotice;
    private bool disposed;

    public EditorFoldingController(
        TextEditor editor,
        EditorDocument document,
        Func<ForgeSnapshot> snapshot,
        FoldingSyntax syntax,
        bool xml,
        Func<CancellationToken, Task<IReadOnlyList<FoldingRange>?>> serverRanges,
        Action<LocText> notice)
    {
        this.editor = editor;
        this.document = document;
        this.snapshot = snapshot;
        this.syntax = syntax;
        this.xml = xml;
        this.serverRanges = serverRanges;
        this.notice = notice;

        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds) };
        timer.Tick += OnTimerTick;
        document.TextChanged += OnTextChanged;
    }

    /// <summary>Whether a folded section exists; the menu enables its unfold commands on it.</summary>
    public bool HasFoldings => manager?.AllFoldings.Any() ?? false;

    /// <summary>Whether any section is currently collapsed.</summary>
    public bool AnyFolded => manager?.AllFoldings.Any(section => section.IsFolded) ?? false;

    /// <summary>Installs the manager and margin; calling it twice is a no-op.</summary>
    public void Enable()
    {
        if (disposed || manager is not null)
        {
            return;
        }

        manager = FoldingManager.Install(editor.TextArea);
        Recompute();
    }

    /// <summary>Removes the manager, its margin and every fold; the fold state is not kept.</summary>
    public void Disable()
    {
        CancelPending();
        timer.Stop();
        var installed = manager;
        manager = null;
        if (installed is not null)
        {
            FoldingManager.Uninstall(installed);
        }

        ReportNotice(default);
    }

    /// <summary>Folds or unfolds the innermost section at the caret; does nothing when the caret is not in one.</summary>
    public bool ToggleAtCaret()
    {
        if (manager is not { } installed)
        {
            return false;
        }

        var offset = Math.Clamp(editor.CaretOffset, 0, document.TextLength);
        foreach (var section in installed.GetFoldingsContaining(offset))
        {
            section.IsFolded = !section.IsFolded;
            return true;
        }

        return false;
    }

    /// <summary>Collapses every section.</summary>
    public void FoldAll()
    {
        if (manager is not { } installed)
        {
            return;
        }

        foreach (var section in installed.AllFoldings)
        {
            section.IsFolded = true;
        }
    }

    /// <summary>Expands every section.</summary>
    public void UnfoldAll()
    {
        if (manager is not { } installed)
        {
            return;
        }

        foreach (var section in installed.AllFoldings)
        {
            section.IsFolded = false;
        }
    }

    /// <summary>Recomputes the ranges now, after an edit that changed the document wholesale.</summary>
    public void Refresh() => Recompute();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        document.TextChanged -= OnTextChanged;
        timer.Tick -= OnTimerTick;
        Disable();
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (disposed || manager is null)
        {
            return;
        }

        timer.Stop();
        timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        timer.Stop();
        Recompute();
    }

    private void Recompute()
    {
        if (disposed || manager is null)
        {
            return;
        }

        CancelPending();
        var cts = new CancellationTokenSource();
        pending = cts;
        var version = document.Version;
        var forge = snapshot();
        _ = RecomputeAsync(forge, version, cts);
    }

    private async Task RecomputeAsync(ForgeSnapshot forge, AvaloniaEdit.Document.ITextSourceVersion version, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            IReadOnlyList<FoldingRange>? ranges = null;
            try
            {
                ranges = await serverRanges(token).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                System.Diagnostics.Trace.WriteLine("[editor] folding ranges failed: " + exception.Message);
                ranges = null;
            }

            if (token.IsCancellationRequested || disposed)
            {
                return;
            }

            if (ranges is null)
            {
                ranges = await ComputeLocallyAsync(forge, token).ConfigureAwait(true);
            }

            if (token.IsCancellationRequested || disposed || document.Version.CompareAge(version) != 0)
            {
                return;
            }

            Apply(ranges);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(pending, cts))
            {
                pending = null;
            }

            cts.Dispose();
        }
    }

    private async Task<IReadOnlyList<FoldingRange>> ComputeLocallyAsync(ForgeSnapshot forge, CancellationToken token)
    {
        if (forge.IsEmpty)
        {
            return [];
        }

        if (forge.LineCount > StructuralFolding.MaxLines)
        {
            ReportNotice(EditorStrings.FoldTooLargeText(StructuralFolding.MaxLines));
            return [];
        }

        ReportNotice(default);
        if (xml)
        {
            // The library's strategy reads the live document, so it runs on the UI thread; the debounce keeps it rare.
            return XmlRanges();
        }

        var syntax = this.syntax;
        return await Task.Run(() => StructuralFolding.Compute(forge, syntax), token).ConfigureAwait(true);
    }

    /// <summary>XML-shaped files fold with the library's own strategy, which understands tags and comments.</summary>
    private IReadOnlyList<FoldingRange> XmlRanges()
    {
        var source = document;
        var strategy = new XmlFoldingStrategy();
        var foldings = strategy.CreateNewFoldings(source, out _);
        var ranges = new List<FoldingRange>();
        foreach (var folding in foldings)
        {
            if (folding.StartOffset < 0 || folding.EndOffset > source.TextLength || folding.EndOffset <= folding.StartOffset)
            {
                continue;
            }

            var start = source.GetLineByOffset(folding.StartOffset).LineNumber - 1;
            var end = source.GetLineByOffset(folding.EndOffset).LineNumber - 1;
            if (end > start)
            {
                ranges.Add(new FoldingRange(start, end));
            }
        }

        return ranges;
    }

    private void Apply(IReadOnlyList<FoldingRange> ranges)
    {
        if (manager is not { } installed)
        {
            return;
        }

        var foldings = new List<NewFolding>(ranges.Count);
        foreach (var range in ranges)
        {
            if (range.StartLine < 0 || range.EndLine >= document.LineCount || !range.IsValid)
            {
                continue;
            }

            var start = document.GetLineByNumber(range.StartLine + 1);
            var end = document.GetLineByNumber(range.EndLine + 1);

            // The first line of a block stays visible; the fold starts at its end and hides up to the close line's end.
            if (end.EndOffset <= start.EndOffset)
            {
                continue;
            }

            foldings.Add(new NewFolding(start.EndOffset, end.EndOffset) { DefaultClosed = false, Name = range.Name ?? string.Empty });
        }

        installed.UpdateFoldings(foldings, -1);
    }

    private void ReportNotice(LocText value)
    {
        if (value.Equals(reportedNotice))
        {
            return;
        }

        reportedNotice = value;
        notice(value);
    }

    private void CancelPending()
    {
        var cts = pending;
        pending = null;
        cts?.Cancel();
        cts?.Dispose();
    }
}
