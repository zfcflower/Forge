using Forge.Workspace;

namespace Forge.App.Git;

/// <summary>Whether the repository GitHub creates for the pairing is private or public.</summary>
internal enum PairingVisibility
{
    Private,
    Public,
}

/// <summary>
/// The "pair a remote" action of a project without one: attaches an existing repository by URL (verified with
/// <c>ls-remote</c> first, so a typo never lands in the config) or creates a new GitHub repository through <c>gh</c> and
/// wires it as <c>origin</c>. Nothing is pushed. Failures come back as a message and change nothing; the caller raises
/// <see cref="RepositoryRemotes.Added"/> on the UI thread after a success so the sessions and panels of that repository learn about it.
/// </summary>
internal sealed class RemotePairing
{
    private const string RemoteName = "origin";

    private static readonly System.Text.RegularExpressions.Regex RemoteHelperUrl = new(
        "^[A-Za-z0-9+.-]+::",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly GitCli git;
    private readonly ConfiguredGh gh;

    public RemotePairing(GitCli git, ConfiguredGh gh)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(gh);
        this.git = git;
        this.gh = gh;
    }

    /// <summary>Verifies that <paramref name="url"/> answers, then adds it as <c>origin</c>.</summary>
    public async Task<GitRemoteResult> PairByUrlAsync(string workingDirectory, string url, CancellationToken ct)
    {
        url = url.Trim();
        if (url.Length == 0 || url[0] == '-' || url.Any(char.IsWhiteSpace))
        {
            return new GitRemoteResult(GitRemoteFailureKind.Other, "The remote URL is empty or malformed.", string.Empty);
        }

        // "<helper>::<address>" runs a remote helper (ext:: runs an arbitrary command); a pasted address never gets that.
        if (RemoteHelperUrl.IsMatch(url))
        {
            return new GitRemoteResult(GitRemoteFailureKind.Other, "Remote-helper URLs (helper::address) are not accepted.", string.Empty);
        }

        // A URL works wherever a remote name does; the ref is only a probe, an absent one is still "reachable".
        var probe = await git.LsRemoteAsync(workingDirectory, url, "main", options: null, ct).ConfigureAwait(false);
        if (!probe.Result.Success)
        {
            return Redacted(probe.Result);
        }

        var added = await git.AddRemoteAsync(workingDirectory, RemoteName, url, ct).ConfigureAwait(false);
        return Redacted(added);
    }

    /// <summary>A message and a stderr that name the address can carry its token (https://token@host/...); they reach a dialog and a log.</summary>
    private static GitRemoteResult Redacted(GitRemoteResult result) =>
        result with { Message = GitCli.RedactCredentials(result.Message), StandardError = GitCli.RedactCredentials(result.StandardError) };

    /// <summary>Creates the repository <paramref name="name"/> on GitHub with <c>gh</c> and adds it as <c>origin</c>; pushes nothing.</summary>
    public async Task<GitRemoteResult> PairByGitHubAsync(
        string workingDirectory,
        string name,
        PairingVisibility visibility,
        CancellationToken ct)
    {
        name = name.Trim();
        if (name.Length == 0 || name[0] == '-' || name.Any(char.IsWhiteSpace))
        {
            return new GitRemoteResult(GitRemoteFailureKind.Other, "The repository name is empty or malformed.", string.Empty);
        }

        var cli = gh.Cli;
        var availability = await cli.GetAvailabilityAsync(workingDirectory, ct).ConfigureAwait(false);
        if (availability.State != GhAvailability.Ready)
        {
            return new GitRemoteResult(GitRemoteFailureKind.Other, availability.Detail, string.Empty);
        }

        var arguments = new List<string>
        {
            "repo", "create", name,
            visibility == PairingVisibility.Public ? "--public" : "--private",
            "--source", ".",
            "--remote", RemoteName,
        };
        var result = await cli.RunAsync(workingDirectory, arguments, null, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            var text = result.StandardError.Length > 0 ? result.StandardError : result.StandardOutput;
            // gh may have created the repository before it failed or timed out, and a retry with the same name then says
            // "already exists"; the message names the way out.
            return new GitRemoteResult(
                result.TimedOut ? GitRemoteFailureKind.TimedOut : GitRemoteFailureKind.Other,
                (result.TimedOut ? "gh repo create timed out." : "gh repo create failed: " + text.Trim())
                + " The repository may already exist on GitHub; if it does, pair it by URL instead of creating it again.",
                result.StandardError);
        }

        return GitRemoteResult.Ok($"Created {name} on GitHub and added it as {RemoteName}.");
    }
}
