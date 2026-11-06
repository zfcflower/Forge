using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// Table-driven tests over real git stderr captured with git 2.56.0 on Windows (messages below are
/// identical under <c>LC_ALL=C</c>, which is what <see cref="GitCli"/> enforces; machine-specific
/// paths from the captures are redacted to <c>D:/...</c> but every classification signal is verbatim).
/// The one exception is the SSH public-key row, which needs a real SSH server: it uses the canonical
/// OpenSSH rejection text a server prints when every key is refused.
/// </summary>
public sealed class GitFailureClassifierTests
{
    public static IEnumerable<object[]> FailureSamples()
    {
        yield return new object[]
        {
            "https auth with wrong credentials",
            128,
            "fatal: Authentication failed for 'http://127.0.0.1:18231/x.git/'\n",
            false,
            GitRemoteFailureKind.AuthenticationFailed,
        };
        yield return new object[]
        {
            "https auth with prompts disabled",
            128,
            "fatal: Cannot prompt because user interactivity has been disabled.\n" +
            "fatal: could not read Username for 'http://127.0.0.1:18231': terminal prompts disabled\n",
            false,
            GitRemoteFailureKind.AuthenticationFailed,
        };
        yield return new object[]
        {
            "ssh public key rejected (canonical server text)",
            128,
            "git@github.com: Permission denied (publickey).\n" +
            "fatal: Could not read from remote repository.\n" +
            "\n" +
            "Please make sure you have the correct access rights\n" +
            "and the repository exists.\n",
            false,
            GitRemoteFailureKind.AuthenticationFailed,
        };
        yield return new object[]
        {
            "unknown remote name",
            128,
            "fatal: 'no-such-remote' does not appear to be a git repository\n" +
            "fatal: Could not read from remote repository.\n" +
            "\n" +
            "Please make sure you have the correct access rights\n" +
            "and the repository exists.\n",
            false,
            GitRemoteFailureKind.RemoteNotFound,
        };
        yield return new object[]
        {
            "remote url pointing at a missing path",
            128,
            "fatal: 'D:/somewhere/repo.git' does not appear to be a git repository\n" +
            "fatal: Could not read from remote repository.\n" +
            "\n" +
            "Please make sure you have the correct access rights\n" +
            "and the repository exists.\n",
            false,
            GitRemoteFailureKind.RemoteNotFound,
        };
        yield return new object[]
        {
            "push rejected as non-fast-forward",
            1,
            "To D:/somewhere/origin.git\n" +
            " ! [rejected]        main -> main (non-fast-forward)\n" +
            "error: failed to push some refs to 'D:/somewhere/origin.git'\n" +
            "hint: Updates were rejected because the tip of your current branch is behind\n" +
            "hint: its remote counterpart. If you want to integrate the remote changes,\n" +
            "hint: use 'git pull' before pushing again.\n" +
            "hint: See the 'Note about fast-forwards' in 'git push --help' for details.\n",
            false,
            GitRemoteFailureKind.NonFastForward,
        };
        yield return new object[]
        {
            "push rejected behind the remote",
            1,
            "To D:/somewhere/origin.git\n" +
            " ! [rejected]        main -> main (fetch first)\n" +
            "error: failed to push some refs to 'D:/somewhere/origin.git'\n" +
            "hint: Updates were rejected because the remote contains work that you do not\n" +
            "hint: have locally. This is usually caused by another repository pushing to\n" +
            "hint: the same ref. If you want to integrate the remote changes, use\n" +
            "hint: 'git pull' before pushing again.\n" +
            "hint: See the 'Note about fast-forwards' in 'git push --help' for details.\n",
            false,
            GitRemoteFailureKind.NonFastForward,
        };
        yield return new object[]
        {
            "force-with-lease against a stale commit",
            1,
            "To D:/somewhere/origin.git\n" +
            " ! [rejected]        main -> main (stale info)\n" +
            "error: failed to push some refs to 'D:/somewhere/origin.git'\n",
            false,
            GitRemoteFailureKind.NonFastForward,
        };
        yield return new object[]
        {
            "pull --ff-only on diverged branches",
            128,
            "From D:/somewhere/origin.git\n" +
            " * branch            main       -> FETCH_HEAD\n" +
            "hint: Diverging branches can't be fast-forwarded, you need to either:\n" +
            "hint: \n" +
            "hint: \tgit merge --no-ff\n" +
            "fatal: Not possible to fast-forward, aborting.\n",
            false,
            GitRemoteFailureKind.NonFastForward,
        };
        yield return new object[]
        {
            "merge stopped on a content conflict",
            1,
            "Auto-merging clash.txt\n" +
            "CONFLICT (content): Merge conflict in clash.txt\n" +
            "Automatic merge failed; fix conflicts and then commit the result.\n",
            false,
            GitRemoteFailureKind.Conflict,
        };
        yield return new object[]
        {
            "rebase stopped on a content conflict",
            1,
            "Rebasing (1/1)\rAuto-merging clash.txt\n" +
            "CONFLICT (content): Merge conflict in clash.txt\n" +
            "error: could not apply 26cdd77... topic\n" +
            "hint: Resolve all conflicts manually, mark them as resolved with\n" +
            "hint: \"git add/rm <conflicted_files>\", then run \"git rebase --continue\".\n" +
            "Could not apply 26cdd77... # topic\n",
            false,
            GitRemoteFailureKind.Conflict,
        };
        yield return new object[]
        {
            "pull with no tracking information",
            1,
            "There is no tracking information for the current branch.\n" +
            "Please specify which branch you want to merge with.\n" +
            "See git-pull(1) for details.\n" +
            "\n" +
            "    git pull <remote> <branch>\n" +
            "\n" +
            "If you wish to set tracking information for this branch you can do so with:\n" +
            "\n" +
            "    git branch --set-upstream-to=origin/<branch> lonely\n",
            false,
            GitRemoteFailureKind.NoUpstream,
        };
        yield return new object[]
        {
            "checkout refused by dirty tracked files",
            1,
            "error: Your local changes to the following files would be overwritten by checkout:\n" +
            "\tclash.txt\n" +
            "Please commit your changes or stash them before you switch branches.\n" +
            "Aborting\n",
            false,
            GitRemoteFailureKind.DirtyWorkTree,
        };
        yield return new object[]
        {
            "unresolvable host",
            128,
            "fatal: unable to access 'https://nonexistent.invalid/x.git/': " +
            "Could not resolve host: nonexistent.invalid\n",
            false,
            GitRemoteFailureKind.Network,
        };
        yield return new object[]
        {
            "ssh connection refused",
            128,
            "ssh: connect to host 127.0.0.1 port 9: Connection refused\r\n" +
            "fatal: Could not read from remote repository.\n" +
            "\n" +
            "Please make sure you have the correct access rights\n" +
            "and the repository exists.\n",
            false,
            GitRemoteFailureKind.Network,
        };
        yield return new object[]
        {
            "timed out overrides failure text",
            -1,
            "Auto-merging clash.txt\n" +
            "CONFLICT (content): Merge conflict in clash.txt\n",
            true,
            GitRemoteFailureKind.TimedOut,
        };
    }

    [Theory]
    [MemberData(nameof(FailureSamples))]
    public void Classify_maps_captured_stderr_to_the_documented_kind(
        string name, int exitCode, string stderr, bool timedOut, GitRemoteFailureKind expected)
    {
        Assert.NotEmpty(name);

        var actual = GitFailureClassifier.Classify(new GitCommandResult(exitCode, string.Empty, stderr, timedOut));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Classify_maps_success_to_none_even_with_warnings_on_stderr()
    {
        var result = GitFailureClassifier.Classify(new GitCommandResult(
            0,
            "9f2c1ab subject\n",
            "warning: in the working copy of 'f.txt', LF will be replaced by CRLF the next time Git touches it\n"));

        Assert.Equal(GitRemoteFailureKind.None, result);
    }

    [Fact]
    public void Classify_maps_a_timeout_with_empty_output_to_timed_out()
    {
        var result = GitFailureClassifier.Classify(new GitCommandResult(-1, string.Empty, string.Empty, TimedOut: true));

        Assert.Equal(GitRemoteFailureKind.TimedOut, result);
    }
}
