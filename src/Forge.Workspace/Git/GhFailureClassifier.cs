namespace Forge.Workspace;

/// <summary>
/// Maps a failed gh invocation to a <see cref="GitRemoteFailureKind"/>; pure, so it is unit-tested on captured text. gh
/// runs under <c>LC_ALL=C</c> and its messages are stable English. Order matters: the first category that matches wins,
/// because one message can mention several (an expired token reports both "HTTP 401" and "authentication"). Text that gh
/// forwards from git (a failed push inside <c>gh pr create</c>) falls through to <see cref="GitFailureClassifier"/>.
/// </summary>
public static class GhFailureClassifier
{
    // gh's message for a missing/invalid login or a token without the needed scope. "gh auth login" is printed whenever
    // no account is configured for a host.
    private static readonly string[] AuthenticationMarkers =
    [
        "gh auth login",
        "not logged in",
        "not logged into",
        "authentication",
        "Bad credentials",
        "token has expired",
        "token in keyring is invalid",
        "required scope",
        "Resource not accessible",
        "Must have admin rights",
        "SAML enforcement",
        "could not read Username",
        "terminal prompts disabled",
        "To get started with GitHub CLI",
        "GH_TOKEN",
    ];

    private static readonly string[] RateLimitMarkers = ["rate limit", "abuse detection"];

    private static readonly string[] PullRequestNotFoundMarkers =
    [
        "no pull requests found",
        "Could not resolve to a PullRequest",
        "pull request not found",
    ];

    private static readonly string[] NoGitHubRemoteMarkers =
    [
        "no git remotes found",
        "none of the git remotes",
    ];

    private static readonly string[] RemoteNotFoundMarkers =
    [
        "Could not resolve to a",
        "No default remote repository",
        "Repository not found",
        "does not appear to be a git repository",
    ];

    private static readonly string[] NetworkMarkers =
    [
        "error connecting to",
        "dial tcp",
        "no such host",
        "Could not resolve host",
        "connection refused",
        "connection reset",
        "i/o timeout",
        "TLS handshake",
        "context deadline exceeded",
        "Client.Timeout",
        "Timeout trying to",
        "network is unreachable",
        "unexpected EOF",
    ];

    private static readonly string[] AlreadyExistsMarkers = ["already exists"];

    public static GitRemoteFailureKind Classify(GhCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.TimedOut)
        {
            return GitRemoteFailureKind.TimedOut;
        }

        if (result.Success)
        {
            return GitRemoteFailureKind.None;
        }

        if (result.StartFailed)
        {
            return GitRemoteFailureKind.Other;
        }

        return ClassifyText(DiagnosticText(result), result.ExitCode);
    }

    /// <summary>True when gh reported that the pull request, or the pull request of the branch, does not exist.</summary>
    public static bool IsPullRequestNotFound(GhCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return !result.Success && !result.TimedOut && !result.StartFailed && IsPullRequestNotFoundText(DiagnosticText(result));
    }

    /// <summary>True when creating a pull request failed because one for the same head and base is already open.</summary>
    public static bool IsPullRequestAlreadyExists(GhCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Success || result.TimedOut || result.StartFailed)
        {
            return false;
        }

        var text = DiagnosticText(result);
        return ContainsAny(text, AlreadyExistsMarkers) && text.Contains("pull request", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPullRequestNotFoundText(string text) => ContainsAny(text, PullRequestNotFoundMarkers);

    /// <summary>Classifies gh's error text on its own, for failures that arrive inside a GraphQL <c>errors</c> array.</summary>
    internal static GitRemoteFailureKind ClassifyText(string text, int exitCode = 1)
    {
        if (ContainsAny(text, RateLimitMarkers))
        {
            return GitRemoteFailureKind.Other;
        }

        // Its message ends with "please use `gh auth login`", which would read as an authentication problem below.
        if (ContainsAny(text, NoGitHubRemoteMarkers))
        {
            return GitRemoteFailureKind.RemoteNotFound;
        }

        if (ContainsAny(text, AuthenticationMarkers) || ContainsHttpStatus(text, "401") || ContainsHttpStatus(text, "403"))
        {
            return GitRemoteFailureKind.AuthenticationFailed;
        }

        // A missing pull request is not a missing repository: callers tell it apart by IsPullRequestNotFound*, and the
        // kind stays Other so nothing reads it as "the remote does not exist".
        if (IsPullRequestNotFoundText(text))
        {
            return GitRemoteFailureKind.Other;
        }

        if (ContainsAny(text, RemoteNotFoundMarkers) || ContainsHttpStatus(text, "404"))
        {
            return GitRemoteFailureKind.RemoteNotFound;
        }

        if (ContainsAny(text, NetworkMarkers)
            || ContainsHttpStatus(text, "502")
            || ContainsHttpStatus(text, "503")
            || ContainsHttpStatus(text, "504"))
        {
            return GitRemoteFailureKind.Network;
        }

        // Whatever is left may be git's own text forwarded by gh.
        return GitFailureClassifier.Classify(new GitCommandResult(exitCode, string.Empty, text));
    }

    /// <summary>gh writes errors to stderr; an error printed to stdout instead is read as a fallback.</summary>
    private static string DiagnosticText(GhCommandResult result)
    {
        if (result.StandardError.Length > 0)
        {
            return result.StandardError;
        }

        return result.StandardOutput.Length <= 4096 ? result.StandardOutput : result.StandardOutput[..4096];
    }

    /// <summary>True when <c>HTTP &lt;status&gt;</c> appears as written by gh, with no further digit after the status.</summary>
    private static bool ContainsHttpStatus(string text, string status)
    {
        var marker = "HTTP " + status;
        var start = 0;
        while (start < text.Length)
        {
            var found = text.IndexOf(marker, start, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            var end = found + marker.Length;
            if (end >= text.Length || !char.IsAsciiDigit(text[end]))
            {
                return true;
            }

            start = found + 1;
        }

        return false;
    }

    private static bool ContainsAny(string text, string[] markers)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
