using System.Globalization;
using System.Text;

namespace Forge.Workspace;

/// <summary>
/// Puts screenshots into the pull request description without ever adding them to the pull request's files. The PNG files become
/// the root of an orphan commit (no parent: <c>hash-object -w</c>, <c>mktree</c>, <c>commit-tree</c>) that is pushed to
/// <c>refs/heads/assets/&lt;branch slug&gt;</c> with a lease taken from a fresh <c>ls-remote</c>, so a previous run's assets are
/// replaced but nobody else's push is overwritten unseen. The <c>## Screenshots</c> section then gets a table of
/// <c>![](https://host/owner/repo/blob/&lt;sha&gt;/&lt;file&gt;?raw=true)</c> images pinned to that commit, written through
/// <see cref="PrBodyEditor"/> (read, edit, re-read, write, read back, verify).
/// <para>
/// Owner, repository and host come from the push remote's address (https, ssh and scp-like forms), because
/// <see cref="IPrPlatform"/> does not expose them. The links are GitHub's; any other address is refused with a clear message
/// rather than guessed.
/// </para>
/// </summary>
public sealed class ScreenshotPublisher(GitCli git, IPrPlatform platform, GitToolSettings settings)
{
    /// <summary>The level-2 section of the description that holds the images.</summary>
    public const string SectionHeading = "Screenshots";

    /// <summary>Every assets branch lives under this prefix, which keeps them apart from real branches.</summary>
    public const string AssetsBranchPrefix = "assets/";

    private const int MaxSlugLength = 80;

    private readonly GitCli git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly IPrPlatform platform = platform ?? throw new ArgumentNullException(nameof(platform));
    private readonly GitToolSettings settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>
    /// The assets branch for a session branch, without <c>refs/heads/</c>: <c>assets/</c> plus the branch name with every character
    /// outside letters, digits and <c>_</c> turned into <c>-</c> (runs collapsed, ends trimmed). Two branch names that differ only in
    /// such characters (<c>a/b</c> and <c>a-b</c>) share one assets branch; each publication replaces the previous one, and the links
    /// in a description stay valid because they name a commit.
    /// </summary>
    public static string AssetsBranchFor(string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        var slug = new StringBuilder(branch.Length);
        foreach (var character in branch)
        {
            var keep = char.IsAsciiLetterOrDigit(character) || character == '_';
            if (keep)
            {
                slug.Append(character);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var text = slug.ToString().Trim('-');
        if (text.Length > MaxSlugLength)
        {
            text = text[..MaxSlugLength].TrimEnd('-');
        }

        return AssetsBranchPrefix + (text.Length == 0 ? "branch" : text);
    }

    /// <summary>
    /// Stores, pushes and links <paramref name="pairs"/> for the branch checked out in <paramref name="worktree"/>.
    /// <paramref name="baseLabel"/> (for example <c>origin/main</c>) only appears in the caption under the table. The caller has
    /// checked that a pull request exists; if it does not, the images are pushed and the result fails at
    /// <see cref="ScreenshotPublishStage.Body"/> with the markdown to use later.
    /// </summary>
    public async Task<ScreenshotPublishResult> PublishAsync(
        string worktree,
        IReadOnlyList<ScreenshotPair> pairs,
        string? baseLabel,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktree);
        ArgumentNullException.ThrowIfNull(pairs);
        if (pairs.Count == 0)
        {
            return Failed(ScreenshotPublishStage.Objects, "There are no images to publish.");
        }

        var branch = await git.GetCurrentBranchAsync(worktree, ct).ConfigureAwait(false);
        if (branch is null)
        {
            return Failed(
                ScreenshotPublishStage.Remote,
                "HEAD is detached: the screenshots are filed under the branch name. Check out the session branch first.");
        }

        var remote = await git.ResolvePushRemoteAsync(worktree, branch, null, ct).ConfigureAwait(false);
        var address = await RemoteAddressAsync(worktree, remote, ct).ConfigureAwait(false);
        var repository = GitHubRepository.Parse(address);
        if (repository is null)
        {
            return Failed(
                ScreenshotPublishStage.Remote,
                address is null
                    ? $"Remote '{remote}' has no address, so the image links cannot be built."
                    : $"Remote '{remote}' ({Redact(address)}) is not a GitHub address (https://host/owner/repo or git@host:owner/repo), so the image links cannot be built.");
        }

        var assetsBranch = AssetsBranchFor(branch);
        var assetsRef = "refs/heads/" + assetsBranch;

        string commit;
        try
        {
            commit = await CreateOrphanCommitAsync(worktree, pairs, branch, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is GitException or ArgumentException)
        {
            return Failed(ScreenshotPublishStage.Objects, "Could not store the images as git objects: " + exception.Message);
        }

        var network = NetworkOptions();
        var observed = await git.LsRemoteAsync(worktree, remote, assetsBranch, network, ct).ConfigureAwait(false);
        if (!observed.Result.Success)
        {
            return Failed(ScreenshotPublishStage.Push, observed.Result.Message, observed.Result.Failure);
        }

        var pushed = await git.PushCommitAsync(worktree, remote, commit, assetsRef, observed.Commit ?? string.Empty, network, ct)
            .ConfigureAwait(false);
        if (!pushed.Success)
        {
            return Failed(ScreenshotPublishStage.Push, pushed.Message, pushed.Failure);
        }

        var caption = baseLabel is null
            ? $"Images are stored on the branch `{assetsBranch}` and are not part of this pull request's files."
            : $"Before: `{baseLabel}`. After: `{branch}`. Images are stored on the branch `{assetsBranch}` and are not part of this pull request's files.";
        var markdown = ScreenshotMarkdown.Render(pairs, repository, commit, caption);

        var edit = await new PrBodyEditor(platform).ReplaceSectionAsync(worktree, SectionHeading, markdown, ct).ConfigureAwait(false);
        if (!edit.Success)
        {
            return new ScreenshotPublishResult(
                false,
                ScreenshotPublishStage.Body,
                $"The images were pushed to {remote} {assetsRef} ({PrLifecycleFailure.Short(commit)}), but the description was not updated: {edit.Status.Message}",
                edit.Status.Failure,
                edit.Reason,
                assetsRef,
                commit,
                markdown);
        }

        return new ScreenshotPublishResult(
            true,
            ScreenshotPublishStage.Done,
            $"Pushed {pairs.Count.ToString(CultureInfo.InvariantCulture)} screen(s) to {remote} {assetsRef} ({PrLifecycleFailure.Short(commit)}). {edit.Status.Message}".TrimEnd(),
            GitRemoteFailureKind.None,
            PrFailureReason.None,
            assetsRef,
            commit,
            markdown,
            edit.Value?.Url);
    }

    private async Task<string> CreateOrphanCommitAsync(
        string worktree,
        IReadOnlyList<ScreenshotPair> pairs,
        string branch,
        CancellationToken ct)
    {
        var entries = new List<GitTreeEntry>(pairs.Count * 2);
        var message = new StringBuilder(256);
        message.Append("Screenshots for ").Append(branch).Append("\n\n");
        foreach (var pair in pairs)
        {
            if (pair.BeforePath is { } before)
            {
                entries.Add(new GitTreeEntry(pair.BeforeFileName, await git.HashObjectAsync(worktree, before, ct).ConfigureAwait(false)));
            }

            if (pair.AfterPath is { } after)
            {
                entries.Add(new GitTreeEntry(pair.AfterFileName, await git.HashObjectAsync(worktree, after, ct).ConfigureAwait(false)));
            }

            message.Append("- ").Append(pair.Name).Append('\n');
        }

        var tree = await git.MakeTreeAsync(worktree, entries, ct).ConfigureAwait(false);
        return await git.CommitTreeAsync(worktree, tree, message.ToString(), parent: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The address the screenshots are pushed to, as written in the configuration (<c>remote.&lt;name&gt;.pushurl</c>, else
    /// <c>.url</c>): the raw value, not one rewritten by <c>url.*.insteadOf</c>, because the web address is what the user typed.
    /// </summary>
    private async Task<string?> RemoteAddressAsync(string worktree, string remote, CancellationToken ct)
    {
        foreach (var key in new[] { "remote." + remote + ".pushurl", "remote." + remote + ".url" })
        {
            if (await git.GetConfigValueAsync(worktree, key, ct).ConfigureAwait(false) is { } value)
            {
                return value;
            }
        }

        foreach (var known in await git.GetRemotesAsync(worktree, ct).ConfigureAwait(false))
        {
            if (string.Equals(known.Name, remote, StringComparison.Ordinal))
            {
                return known.PushUrl ?? known.FetchUrl;
            }
        }

        return null;
    }

    /// <summary>The address without the user name and password of a URL, so a token in a remote never reaches a message.</summary>
    internal static string Redact(string address)
    {
        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            return address;
        }

        var authorityStart = scheme + 3;
        var authorityEnd = address.IndexOf('/', authorityStart);
        if (authorityEnd < 0)
        {
            authorityEnd = address.Length;
        }

        var at = address.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
        return at < 0 ? address : address[..authorityStart] + address[(at + 1)..];
    }

    private GitNetworkOptions NetworkOptions()
    {
        var timeout = settings.NetworkTimeout();
        return timeout > TimeSpan.Zero ? new GitNetworkOptions { Timeout = timeout } : new GitNetworkOptions();
    }

    private static ScreenshotPublishResult Failed(
        ScreenshotPublishStage stage,
        string message,
        GitRemoteFailureKind failure = GitRemoteFailureKind.Other) =>
        new(false, stage, message, failure);
}

/// <summary>Renders the content of the <c>## Screenshots</c> section.</summary>
public static class ScreenshotMarkdown
{
    /// <summary>
    /// A table with one row per screen (<c>Screen | Before | After</c>), each cell an image link pinned to <paramref name="commit"/>;
    /// a side without an image says so in italics. <paramref name="caption"/>, when given, follows the table as a plain paragraph.
    /// </summary>
    public static string Render(IReadOnlyList<ScreenshotPair> pairs, GitHubRepository repository, string commit, string? caption = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);

        var text = new StringBuilder(256 + pairs.Count * 320);
        text.Append("| Screen | Before | After |\n| --- | --- | --- |\n");
        foreach (var pair in pairs)
        {
            text.Append("| ").Append(pair.Name).Append(" | ");
            if (pair.HasBefore)
            {
                text.Append("![").Append(pair.Name).Append(" before](").Append(repository.RawImageUrl(commit, pair.BeforeFileName)).Append(')');
            }
            else
            {
                text.Append("_no image on the base_");
            }

            text.Append(" | ");
            if (pair.HasAfter)
            {
                text.Append("![").Append(pair.Name).Append(" after](").Append(repository.RawImageUrl(commit, pair.AfterFileName)).Append(')');
            }
            else
            {
                text.Append("_no image after the change_");
            }

            text.Append(" |\n");
        }

        if (!string.IsNullOrWhiteSpace(caption))
        {
            text.Append('\n').Append(caption.Trim()).Append('\n');
        }

        return text.ToString();
    }
}
