using System.ComponentModel;
using System.Windows.Input;
using Forge.App.Changes;
using Forge.Workspace;

namespace Forge.App.Tests.Changes;

/// <summary>Diff fixtures and fake file/block items shared by the diff editor tests.</summary>
internal static class DiffFixtures
{
    /// <summary>
    /// A full-context diff of a <paramref name="lineCount"/> line file whose line <paramref name="changed"/> was replaced
    /// (old "old{n}", new "new{n}"). Context lines read "line{n}".
    /// </summary>
    public static FileDiff Replace(string path, int lineCount = 20, int changed = 10)
    {
        var lines = new List<DiffLine>();
        for (var n = 1; n <= lineCount; n++)
        {
            if (n == changed)
            {
                lines.Add(new DiffLine(DiffLineKind.Removed, n, 0, "old" + n));
                lines.Add(new DiffLine(DiffLineKind.Added, 0, n, "new" + n));
            }
            else
            {
                lines.Add(new DiffLine(DiffLineKind.Context, n, n, "line" + n));
            }
        }

        return new FileDiff(path, path, FileDiffStatus.Modified, false, [new DiffHunk(1, lineCount, 1, lineCount, lines)]);
    }

    /// <summary>One hunk where <paramref name="removed"/> old lines became <paramref name="added"/> new lines, after 3 context lines.</summary>
    public static FileDiff Uneven(string path, int removed, int added)
    {
        var lines = new List<DiffLine>();
        for (var n = 1; n <= 3; n++)
        {
            lines.Add(new DiffLine(DiffLineKind.Context, n, n, "line" + n));
        }

        for (var k = 0; k < removed; k++)
        {
            lines.Add(new DiffLine(DiffLineKind.Removed, 4 + k, 0, "old" + k));
        }

        for (var k = 0; k < added; k++)
        {
            lines.Add(new DiffLine(DiffLineKind.Added, 0, 4 + k, "new" + k));
        }

        return new FileDiff(path, path, FileDiffStatus.Modified, false, [new DiffHunk(1, 3 + removed, 1, 3 + added, lines)]);
    }

    /// <summary>Two hunks of a 40 line file: line 2 replaced and line 30 replaced (git style, 3 lines of context).</summary>
    public static FileDiff TwoHunks(string path)
    {
        var first = new List<DiffLine>
        {
            new(DiffLineKind.Context, 1, 1, "line1"),
            new(DiffLineKind.Removed, 2, 0, "old2"),
            new(DiffLineKind.Added, 0, 2, "new2"),
            new(DiffLineKind.Context, 3, 3, "line3"),
            new(DiffLineKind.Context, 4, 4, "line4"),
            new(DiffLineKind.Context, 5, 5, "line5"),
        };
        var second = new List<DiffLine>
        {
            new(DiffLineKind.Context, 27, 27, "line27"),
            new(DiffLineKind.Context, 28, 28, "line28"),
            new(DiffLineKind.Context, 29, 29, "line29"),
            new(DiffLineKind.Removed, 30, 0, "old30"),
            new(DiffLineKind.Added, 0, 30, "new30"),
            new(DiffLineKind.Context, 31, 31, "line31"),
        };
        return new FileDiff(path, path, FileDiffStatus.Modified, false,
            [new DiffHunk(1, 5, 1, 5, first), new DiffHunk(27, 5, 27, 5, second)]);
    }

    /// <summary>A pure deletion of old lines 5-6 after line 4 (zero new-side count).</summary>
    public static FileDiff DeletionOnly(string path)
    {
        var lines = new List<DiffLine>
        {
            new(DiffLineKind.Context, 4, 4, "line4"),
            new(DiffLineKind.Removed, 5, 0, "gone5"),
            new(DiffLineKind.Removed, 6, 0, "gone6"),
        };
        return new FileDiff(path, path, FileDiffStatus.Modified, false, [new DiffHunk(4, 3, 4, 1, lines)]);
    }

    public static FakeFile File(string path, FileDiff? diff = null, bool fullContext = true) =>
        new(diff ?? Replace(path), fullContext);

    public static List<FakeFile> Files(int count, int lines = 20) =>
        [.. Enumerable.Range(0, count).Select(i => File("src/f" + i + ".cs", Replace("src/f" + i + ".cs", lines)))];
}

internal sealed class NoopCommand : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter)
    {
    }
}

internal sealed class FakeBlock(ChangeBlock block) : IDiffBlockItem
{
    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }

    public ChangeBlock Block { get; } = block;

    public bool IsKept { get; set; }

    public ICommand KeepCommand { get; } = new NoopCommand();

    public ICommand RevertCommand { get; } = new NoopCommand();
}

internal sealed class FakeFile : IDiffFileItem
{
    private bool collapsed;
    private bool preview = true;
    private bool viewed;
    private IReadOnlyList<IDiffBlockItem> blocks;

    public FakeFile(FileDiff diff, bool fullContext = true)
    {
        Diff = diff;
        HasFullContext = fullContext;
        blocks = [.. DiffBlocks.Split(diff).Select(static block => new FakeBlock(block))];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FileDiff Diff { get; private set; }

    public string Path => Diff.Path.Replace('\\', '/');

    public string Badge => "M";

    public int Added => Diff.AddedLineCount;

    public int Removed => Diff.RemovedLineCount;

    public IReadOnlyList<IDiffBlockItem> Blocks => blocks;

    public int KeptCount => 0;

    public string KeptSummary => "0/" + blocks.Count;

    public bool IsViewed
    {
        get => viewed;
        set => Set(ref viewed, value, nameof(IsViewed));
    }

    public bool IsCollapsed
    {
        get => collapsed;
        set => Set(ref collapsed, value, nameof(IsCollapsed));
    }

    public bool IsMarkdown => Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    public bool IsMarkdownPreview
    {
        get => preview;
        set => Set(ref preview, value, nameof(IsMarkdownPreview));
    }

    public bool HasFullContext { get; }

    public bool CanLoadContext => false;

    public Task<bool> LoadFullContextAsync() => Task.FromResult(HasFullContext);

    public bool CanRevert => true;

    public ICommand RevertFileCommand { get; } = new NoopCommand();

    public ICommand OpenInEditorCommand { get; } = new NoopCommand();

    public ICommand CopyPathCommand { get; } = new NoopCommand();

    public ICommand ToggleCollapsedCommand { get; } = new NoopCommand();

    /// <summary>Replaces the diff and the blocks, as a revert of a block would.</summary>
    public void ReplaceDiff(FileDiff diff)
    {
        Diff = diff;
        blocks = [.. DiffBlocks.Split(diff).Select(static block => new FakeBlock(block))];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Diff)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Blocks)));
    }

    private void Set(ref bool field, bool value, string name)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
