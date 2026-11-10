namespace Forge.App.ViewModels;

/// <summary>
/// What the shell needs to open the new-branch dialog: the repository whose branches are offered as the start point,
/// and the branch name to prefill. The dialog owns the branch list and its validation, so the tree only asks and waits.
/// </summary>
internal sealed record NewBranchRequest(string RepositoryPath, string SuggestedName);

/// <summary>
/// The user's answer in the new-branch dialog: <see cref="StartPoint"/> is the ref the new branch starts at (a local
/// branch name, or a remote-tracking ref such as <c>origin/main</c>), <see cref="BranchName"/> the name of the new
/// branch exactly as typed. A cancelled dialog yields null instead, and nothing is created.
/// </summary>
internal sealed record NewBranchChoice(string StartPoint, string BranchName);
