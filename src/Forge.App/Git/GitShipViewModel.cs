using System.Collections.ObjectModel;
using Forge.App.ViewModels;

using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>
/// What the merge dialog is asked about (which pull request, which branch into which base) and hands back (the strategy
/// the user picked, and whether the head branch should be deleted on the platform afterwards).
/// </summary>
internal sealed record PullRequestMergeRequest(int Number, string HeadBranch, string BaseBranch);

internal sealed record PullRequestMergeChoice(PrMergeMethod Method, bool DeleteBranch);

/// <summary>What the pull-request dialog hands back to the row: the three things the platform takes.</summary>
internal sealed record PullRequestDraft(string Title, string Body, bool Draft);

/// <summary>
/// The four states a pull request puts the row in, plus the two "we do not know" cases: no gh, or the read failed. The
/// distinction matters — <see cref="Unavailable"/> must not be drawn as "no pull request", because the menu then offers to
/// create one that may already exist.
/// </summary>
internal enum PullRequestPhase
{
    /// <summary>Nothing has been read yet (attached, read in flight).</summary>
    Unknown,

    /// <summary>Read, and this branch has no pull request.</summary>
    None,

    /// <summary>Read, and there is one: open, draft, merged or closed.</summary>
    Open,
    Draft,
    Merged,
    Closed,

    /// <summary>gh is missing, not signed in, or the read failed; the local repository is unaffected.</summary>
    Unavailable,
}

/// <summary>
/// One row of the 提交目标 menu. Rows are never removed while the menu is built — an action that does not apply right now is
/// disabled and says why, so the menu keeps its shape between states.
/// </summary>
internal sealed class ShipActionViewModel(
    string key,
    RelayCommand command,
    Func<bool> enabled,
    Func<string> label,
    Func<string> hint)
{
    /// <summary>Stable identity for tests and for the view's AutomationId (<c>git-ship-&lt;key&gt;</c>).</summary>
    public string Key { get; } = key;

    public RelayCommand Command { get; } = command;

    public string Label => label();

    public string Hint => hint();

    public bool IsEnabled => enabled();

    /// <summary>The reason shown under a disabled row; empty when the row is usable.</summary>
    public string DisabledReason => IsEnabled ? string.Empty : hint();
}
