using System.Windows.Input;
using Forge.App.Changes;
using Forge.App.Localization;
using Forge.Workspace;

namespace Forge.App.ViewModels;

/// <summary>One change block of a file in the changes document: the "✓ 保留  ↶" bar's state and commands.</summary>
internal sealed class ChangeBlockItemViewModel : ObservableObject, IDiffBlockItem
{
    private readonly FileDiffViewModel file;
    private bool isKept;

    public ChangeBlockItemViewModel(FileDiffViewModel file, ChangeBlock block)
    {
        this.file = file;
        Block = block;
        KeepCommand = new RelayCommand(() => file.Owner?.ToggleKept(file, this), () => file.Owner is not null);
        RevertCommand = new RelayCommand(() => file.Owner?.RevertBlock(file, this), () => file.Owner is not null && file.CanRevert);
    }

    public ChangeBlock Block { get; }

    public bool IsKept => isKept;

    public RelayCommand KeepCommand { get; }

    public RelayCommand RevertCommand { get; }

    ICommand IDiffBlockItem.KeepCommand => KeepCommand;

    ICommand IDiffBlockItem.RevertCommand => RevertCommand;

    /// <summary>Carries the kept state to a replacement block item before anyone observes it.</summary>
    internal void SetKeptSilently(bool value) => isKept = value;

    internal void SetKept(bool value)
    {
        if (isKept == value)
        {
            return;
        }

        isKept = value;
        OnPropertyChanged(nameof(IsKept));
        file.OnKeptChanged();
    }

    internal void NotifyOwnerChanged()
    {
        KeepCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>The review half of <see cref="FileDiffViewModel"/>: blocks, viewed/kept marks and the header commands.</summary>
internal sealed partial class FileDiffViewModel : ObservableObject, IDiffFileItem
{
    private static readonly string[] MarkdownExtensions = [".md", ".markdown", ".mdx"];

    private IReadOnlyList<ChangeBlockItemViewModel> blocks = [];
    private ChangesViewModel? owner;
    private bool isViewed;
    private bool isCollapsed;
    private bool isMarkdownPreview = true;
    private bool hasFullContext;
    private Task<bool>? contextLoad;

    public FileDiff Diff { get; private set; }

    internal static bool IsMarkdownPath(string path)
    {
        var extension = System.IO.Path.GetExtension(path);
        return MarkdownExtensions.Any(candidate => string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Content fingerprint the "viewed" mark is stored against (<see cref="DiffBlocks.Fingerprint"/>).</summary>
    public string Fingerprint { get; private set; } = string.Empty;

    public IReadOnlyList<ChangeBlockItemViewModel> Blocks => blocks;

    IReadOnlyList<IDiffBlockItem> IDiffFileItem.Blocks => blocks;

    int IDiffFileItem.Added => added;

    int IDiffFileItem.Removed => removed;

    public string AddedText => "+" + TextFormat.Number(added);

    public string RemovedText => "−" + TextFormat.Number(removed);

    public int KeptCount => blocks.Count(static block => block.IsKept);

    public string KeptSummary => ChangesReviewStrings.KeptSummary(KeptCount, blocks.Count);

    public bool HasBlocks => blocks.Count > 0;

    public bool IsViewed
    {
        get => isViewed;
        set
        {
            if (SetViewed(value))
            {
                owner?.OnViewedToggled(this);
            }
        }
    }

    public bool IsCollapsed
    {
        get => isCollapsed;
        set => Set(ref isCollapsed, value);
    }

    public bool IsMarkdown { get; private set; }

    public bool IsMarkdownPreview
    {
        get => isMarkdownPreview;
        set => Set(ref isMarkdownPreview, value);
    }

    public bool HasFullContext => hasFullContext;

    /// <summary>The unmodified stretches are not loaded yet but can be: opening one reads the whole file once.</summary>
    public bool CanLoadContext => !hasFullContext && owner is { CanLoadContext: true } && !Diff.IsBinary && !Diff.IsTruncated;

    /// <summary>Loads the whole-file diff once (concurrent callers share the read); true once <see cref="Diff"/> carries it.</summary>
    public Task<bool> LoadFullContextAsync()
    {
        if (hasFullContext)
        {
            return Task.FromResult(true);
        }

        if (contextLoad is { IsCompleted: false } running)
        {
            return running;
        }

        return contextLoad = owner is { CanLoadContext: true } document ? document.LoadFullContextAsync(this) : Task.FromResult(false);
    }

    /// <summary>
    /// Swaps in the whole-file diff of the same changes (UI thread). Blocks keep their kept state; nothing else changes
    /// because blocks, fingerprint and counts depend on the changed lines only. False when the changes differ (file edited).
    /// </summary>
    internal bool ApplyFullContext(FileDiff full)
    {
        var split = DiffBlocks.Split(full);
        if (!string.Equals(DiffBlocks.Fingerprint(full, split), Fingerprint, StringComparison.Ordinal) || split.Count != blocks.Count)
        {
            return false;
        }

        var items = new ChangeBlockItemViewModel[split.Count];
        for (var index = 0; index < split.Count; index++)
        {
            items[index] = new ChangeBlockItemViewModel(this, split[index]);
            items[index].SetKeptSilently(blocks[index].IsKept);
        }

        Diff = full;
        blocks = items;
        hasFullContext = !full.IsBinary && !full.IsTruncated && full.Hunks.Count <= 1;
        OnPropertyChanged(nameof(HasFullContext));
        OnPropertyChanged(nameof(CanLoadContext));
        OnPropertyChanged(nameof(Blocks));
        OnPropertyChanged(nameof(Diff));
        return true;
    }

    /// <summary>Revert edits the working file, so it needs a diff whose new side is the working tree (see <see cref="ChangesViewModel.FilesRevertable"/>).</summary>
    public bool CanRevert => owner is { FilesRevertable: true } && !Diff.IsBinary && !Diff.IsTruncated && !Diff.IsRenameOrCopy;

    public RelayCommand RevertFileCommand { get; private set; } = null!;

    public RelayCommand OpenInEditorCommand { get; private set; } = null!;

    public RelayCommand CopyPathCommand { get; private set; } = null!;

    public RelayCommand ToggleCollapsedCommand { get; private set; } = null!;

    public RelayCommand ShowPreviewCommand { get; private set; } = null!;

    public RelayCommand ShowSourceCommand { get; private set; } = null!;

    ICommand IDiffFileItem.RevertFileCommand => RevertFileCommand;

    ICommand IDiffFileItem.OpenInEditorCommand => OpenInEditorCommand;

    ICommand IDiffFileItem.CopyPathCommand => CopyPathCommand;

    ICommand IDiffFileItem.ToggleCollapsedCommand => ToggleCollapsedCommand;

    internal ChangesViewModel? Owner => owner;

    private void InitializeReview(FileDiff diff, bool fullContext)
    {
        var split = DiffBlocks.Split(diff);
        var items = new ChangeBlockItemViewModel[split.Count];
        for (var index = 0; index < split.Count; index++)
        {
            items[index] = new ChangeBlockItemViewModel(this, split[index]);
        }

        blocks = items;
        Fingerprint = DiffBlocks.Fingerprint(diff, split);
        IsMarkdown = IsMarkdownPath(diff.Path);

        // A full-context read puts the whole file into one hunk (git never splits it); added/deleted files are whole anyway.
        hasFullContext = !diff.IsBinary && !diff.IsTruncated &&
            (diff.Status is FileDiffStatus.Added or FileDiffStatus.Deleted || (fullContext && diff.Hunks.Count <= 1));
        RevertFileCommand = new RelayCommand(() => owner?.RevertFile(this), () => owner is not null && CanRevert);
        OpenInEditorCommand = new RelayCommand(() => owner?.OpenInEditor(this), () => owner is not null && Diff.Status != FileDiffStatus.Deleted);
        CopyPathCommand = new RelayCommand(() => owner?.RequestCopyPath(this), () => owner is not null);
        ToggleCollapsedCommand = new RelayCommand(() => IsCollapsed = !IsCollapsed);
        ShowPreviewCommand = new RelayCommand(() => IsMarkdownPreview = true);
        ShowSourceCommand = new RelayCommand(() => IsMarkdownPreview = false);
    }

    /// <summary>Hands the file to its document (UI thread, after the build) so its commands can act.</summary>
    internal void AttachOwner(ChangesViewModel? value)
    {
        owner = value;
        RevertFileCommand.NotifyCanExecuteChanged();
        OpenInEditorCommand.NotifyCanExecuteChanged();
        CopyPathCommand.NotifyCanExecuteChanged();
        foreach (var block in blocks)
        {
            block.NotifyOwnerChanged();
        }
    }

    /// <summary>Sets the viewed state without persisting it (mark loading and "mark all"). Viewing collapses the body.</summary>
    internal bool SetViewed(bool value)
    {
        if (!Set(ref isViewed, value, nameof(IsViewed)))
        {
            return false;
        }

        IsCollapsed = value;
        return true;
    }

    internal void OnKeptChanged()
    {
        OnPropertyChanged(nameof(KeptCount));
        OnPropertyChanged(nameof(KeptSummary));
    }

    internal void RefreshReviewText() => OnPropertyChanged(nameof(KeptSummary));
}
