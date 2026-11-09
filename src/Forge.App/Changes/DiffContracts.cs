using System.ComponentModel;
using System.Windows.Input;
using Forge.App.Review;
using Forge.Workspace;

namespace Forge.App.Changes;

internal enum DiffViewMode
{
    Inline = 0,
    Split = 1,
}

internal enum DiffLayout
{
    Stacked = 0,
    Single = 1,
}

/// <summary>One change block as the diff editor and the Markdown preview show it (the "✓ 保留  ↶" bar above it).</summary>
internal interface IDiffBlockItem : INotifyPropertyChanged
{
    ChangeBlock Block { get; }

    /// <summary>The user kept (accepted) this block; persisted per session against <see cref="ChangeBlock.Hash"/>.</summary>
    bool IsKept { get; }

    /// <summary>Toggles <see cref="IsKept"/>.</summary>
    ICommand KeepCommand { get; }

    /// <summary>Reverts this block in the working tree to its old side; disabled when the file cannot be reverted.</summary>
    ICommand RevertCommand { get; }
}

/// <summary>One file of the changes document, as the diff editor (header bar, body) and Markdown preview consume it.</summary>
internal interface IDiffFileItem : INotifyPropertyChanged
{
    FileDiff Diff { get; }

    /// <summary>Session-relative path with '/' separators; also the review-comment file path.</summary>
    string Path { get; }

    /// <summary>A/M/D/R/C.</summary>
    string Badge { get; }

    int Added { get; }

    int Removed { get; }

    IReadOnlyList<IDiffBlockItem> Blocks { get; }

    int KeptCount { get; }

    /// <summary>"已保留 3/17" text for the header.</summary>
    string KeptSummary { get; }

    bool IsViewed { get; set; }

    /// <summary>The body is folded away (header only). Checking "已查看" also collapses the file.</summary>
    bool IsCollapsed { get; set; }

    bool IsMarkdown { get; }

    /// <summary>Markdown files: rendered preview (true, default) or Source diff (false).</summary>
    bool IsMarkdownPreview { get; set; }

    /// <summary>
    /// The diff carries whole files (full-context read), so the unchanged regions can be expanded. When false only git's
    /// 3-line hunk context exists and the "N unmodified lines" bars are not expandable.
    /// </summary>
    bool HasFullContext { get; }

    bool CanRevert { get; }

    ICommand RevertFileCommand { get; }

    ICommand OpenInEditorCommand { get; }

    ICommand CopyPathCommand { get; }

    ICommand ToggleCollapsedCommand { get; }
}

/// <summary>Everything the diff editor needs to create review comments; null disables commenting.</summary>
internal sealed record DiffReviewContext(ReviewServices Services, ReviewOwner Owner, string? BaseCommit);
