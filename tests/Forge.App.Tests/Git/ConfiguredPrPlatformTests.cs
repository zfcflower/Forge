using System.Text;
using Forge.App.Git;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins <see cref="ConfiguredPrPlatform"/>: the <see cref="IPrPlatform"/> the app hands to the UI forwards every call to the
/// platform behind a <see cref="ConfiguredGh"/>. The merge and mark-ready forwards are the ones that once went missing —
/// the UI's 合并 PR then silently answered with <see cref="IPrPlatform"/>'s own default ("This platform cannot merge pull
/// requests.") instead of merging — so they are pinned twice: a call with a gh that cannot start must reach the platform
/// layer and come back with its own diagnosis rather than that default text, and a call against a scripted gh must arrive
/// with the pull request number, the <see cref="PrMergeMethod"/> and the delete-branch flag, and the platform's result must
/// come back. Nothing here talks to GitHub: the first pair never starts a process, the second points at a stand-in script.
/// </summary>
public sealed class ConfiguredPrPlatformTests
{
    private const string MergeDefault = "This platform cannot merge pull requests.";
    private const string ReadyDefault = "This platform cannot mark a pull request ready.";

    // ------------------------------------------------------------ the regression that must never come back

    [Fact]
    public async Task Merge_is_not_answered_with_the_interface_default()
    {
        using var temp = new TempDir();
        var platform = PlatformOver(Path.Combine(temp.Path, "no-such-gh"));

        var merged = await platform.MergePullRequestAsync(temp.Path, 12, PrMergeMethod.Squash, deleteBranch: true);

        // The call reached the platform layer, which could not start gh; the interface's own "cannot merge" is not the answer.
        Assert.Equal(PrFailureReason.PlatformNotInstalled, merged.Reason);
        Assert.NotEqual(MergeDefault, merged.Status.Message);
        Assert.Contains("could not be started", merged.Status.Message, StringComparison.Ordinal);
        Assert.Null(merged.Value);
    }

    [Fact]
    public async Task Ready_is_not_answered_with_the_interface_default()
    {
        using var temp = new TempDir();
        var platform = PlatformOver(Path.Combine(temp.Path, "no-such-gh"));

        var ready = await platform.MarkPullRequestReadyAsync(temp.Path, 12);

        Assert.Equal(PrFailureReason.PlatformNotInstalled, ready.Reason);
        Assert.NotEqual(ReadyDefault, ready.Status.Message);
        Assert.Contains("could not be started", ready.Status.Message, StringComparison.Ordinal);
        Assert.Null(ready.Value);
    }

    // ------------------------------------------------------------ forwarding, against a scripted gh

    [Theory]
    [InlineData(PrMergeMethod.Squash, "--squash", false)]
    [InlineData(PrMergeMethod.Merge, "--merge", true)]
    [InlineData(PrMergeMethod.Rebase, "--rebase", true)]
    public async Task Merge_forwards_the_number_the_method_and_the_delete_flag(PrMergeMethod method, string flag, bool deleteBranch)
    {
        using var gh = new ScriptedGh();
        var platform = PlatformOver(gh.Executable);

        var merged = await platform.MergePullRequestAsync(gh.WorkingDirectory, 12, method, deleteBranch);

        Assert.True(merged.Success, merged.Status.Message + merged.Status.StandardError);
        Assert.Equal(12, merged.Value!.Number);
        Assert.Equal("pr merge 12 " + flag + (deleteBranch ? " --delete-branch" : string.Empty), Assert.Single(gh.Calls));
    }

    [Fact]
    public async Task Ready_forwards_the_number_and_returns_the_platform_result()
    {
        using var gh = new ScriptedGh();
        var platform = PlatformOver(gh.Executable);

        var ready = await platform.MarkPullRequestReadyAsync(gh.WorkingDirectory, 7);

        Assert.True(ready.Success, ready.Status.Message + ready.Status.StandardError);
        Assert.Equal(7, ready.Value!.Number);
        Assert.Equal(string.Empty, ready.Value.Url);
        Assert.Equal("pr ready 7", Assert.Single(gh.Calls));
    }

    // ------------------------------------------------------------ harness

    /// <summary>
    /// The forwarding platform over a <see cref="ConfiguredGh"/> that names <paramref name="executable"/> and no other
    /// preference, built the way the composition root builds it.
    /// </summary>
    private static IPrPlatform PlatformOver(string executable) =>
        new ConfiguredPrPlatform(new ConfiguredGh(() => executable, new GitCli(), () => TimeSpan.FromSeconds(30)));

    /// <summary>A throw-away directory deleted with the test.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = TestTempFiles.NewDirectory("forge-configured-gh-");

        public string Path { get; }

        public void Dispose() => TestTempFiles.DeleteTree(Path);
    }

    /// <summary>
    /// A stand-in for the gh executable (.cmd on Windows, a shell script elsewhere): it appends the arguments of every call
    /// to <c>args.log</c>, prints one line and exits 0. Only the <see cref="GhCli"/> in the tested object starts it.
    /// </summary>
    private sealed class ScriptedGh : IDisposable
    {
        private static readonly UTF8Encoding Utf8 = new(false);

        private readonly string root;
        private readonly string scriptDirectory;

        public ScriptedGh()
        {
            root = TestTempFiles.NewDirectory("forge-configured-gh-script-");
            scriptDirectory = System.IO.Path.Combine(root, "fake");
            Directory.CreateDirectory(scriptDirectory);
            WorkingDirectory = System.IO.Path.Combine(root, "work");
            Directory.CreateDirectory(WorkingDirectory);
            Executable = WriteScript();
        }

        public string Executable { get; }

        public string WorkingDirectory { get; }

        /// <summary>One entry per invocation: the arguments as the script saw them.</summary>
        public IReadOnlyList<string> Calls
        {
            get
            {
                var path = System.IO.Path.Combine(scriptDirectory, "args.log");
                return File.Exists(path)
                    ? File.ReadAllLines(path).Select(line => line.TrimEnd()).Where(line => line.Length > 0).ToList()
                    : [];
            }
        }

        public void Dispose() => TestTempFiles.DeleteTree(root);

        private string WriteScript()
        {
            if (OperatingSystem.IsWindows())
            {
                var path = System.IO.Path.Combine(scriptDirectory, "gh.cmd");
                const string script = """
                    @echo off
                    echo %* >>"%~dp0args.log"
                    echo done
                    exit /b 0
                    """;
                File.WriteAllText(path, script.ReplaceLineEndings("\r\n") + "\r\n", Encoding.ASCII);
                return path;
            }

            var unix = System.IO.Path.Combine(scriptDirectory, "gh");
            const string unixScript = """
                #!/bin/sh
                echo "$@" >>"$(dirname "$0")/args.log"
                echo done
                exit 0
                """;
            File.WriteAllText(unix, unixScript.ReplaceLineEndings("\n") + "\n", Utf8);
            File.SetUnixFileMode(unix, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return unix;
        }
    }
}
