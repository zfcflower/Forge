using Forge.Abstractions;
using Forge.IO.Tools;
using Xunit;

namespace Forge.IO.Tests;

/// <summary>
/// The <c>bash</c> tool forwards <see cref="ToolContext.Progress"/> to the process request, so a watcher sees the live tail
/// while the finished result still carries the full output; without a watcher nothing about the result changes.
/// </summary>
public class BashToolProgressTests
{
    private static readonly string EchoCommand = OperatingSystem.IsWindows() ? "cmd /c echo hello-progress" : "echo hello-progress";

    private static readonly string FailingCommand = OperatingSystem.IsWindows()
        ? "echo before-failure && cmd /c exit 3"
        : "echo before-failure && sh -c \"exit 3\"";

    private static BashTool Tool() => new(new ProcessRunner(), static () => ToolOptions.Default);

    private static async Task<ToolResult> RunAsync(TemporaryWorkspace workspace, string command, Action<string>? progress)
    {
        var context = workspace.Context with { Progress = progress };
        var call = new ToolCall("call-1", "bash", TemporaryWorkspace.Json(new { command }));
        return await Tool().InvokeAsync(call, context, CancellationToken.None);
    }

    [Fact]
    public async Task A_command_that_prints_reports_a_tail_that_the_final_result_contains()
    {
        using var workspace = new TemporaryWorkspace();
        var tails = new List<string>();

        var result = await RunAsync(workspace, EchoCommand, tail =>
        {
            lock (tails)
            {
                tails.Add(tail);
            }
        });

        Assert.False(result.IsError, result.Content);
        string[] seen;
        lock (tails)
        {
            seen = [.. tails];
        }

        Assert.NotEmpty(seen);
        Assert.Contains("hello-progress", seen[^1], StringComparison.Ordinal);
        Assert.Contains(seen[^1].Trim(), result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_mode_command_reports_progress_too()
    {
        using var workspace = new TemporaryWorkspace();
        var tails = new List<string>();

        var result = await RunAsync(workspace, "echo first && echo second", tail =>
        {
            lock (tails)
            {
                tails.Add(tail);
            }
        });

        Assert.Contains("mode: script (", result.Content, StringComparison.Ordinal);
        string last;
        lock (tails)
        {
            Assert.NotEmpty(tails);
            last = tails[^1];
        }

        // Text parked on the interval timer when the run ends is dropped on purpose (the result carries everything), so the
        // last tail may stop early; it is still a prefix of the full stdout, never something the result does not contain.
        Assert.Contains("first", last, StringComparison.Ordinal);
        Assert.Contains(last.Trim(), result.Content, StringComparison.Ordinal);
        Assert.StartsWith("first", last.TrimStart(), StringComparison.Ordinal);
        Assert.True(result.Content.IndexOf("second", StringComparison.Ordinal) > result.Content.IndexOf("first", StringComparison.Ordinal));
        Assert.Contains("first", result.Content, StringComparison.Ordinal);
        Assert.Contains("second", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_progress_sink_the_result_is_the_same()
    {
        using var workspace = new TemporaryWorkspace();
        var watched = new List<string>();

        var plain = await RunAsync(workspace, EchoCommand, progress: null);
        var observed = await RunAsync(workspace, EchoCommand, tail =>
        {
            lock (watched)
            {
                watched.Add(tail);
            }
        });

        Assert.False(plain.IsError);
        Assert.Equal(StripDuration(plain.Content), StripDuration(observed.Content));
        Assert.Contains("hello-progress", plain.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_progress_sink_that_throws_does_not_fail_the_tool()
    {
        using var workspace = new TemporaryWorkspace();
        var calls = 0;

        var result = await RunAsync(workspace, EchoCommand, _ =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("listener failed");
        });

        Assert.False(result.IsError, result.Content);
        Assert.True(Volatile.Read(ref calls) > 0);
        Assert.Contains("hello-progress", result.Content, StringComparison.Ordinal);
        Assert.Contains("exit: 0", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_command_still_reports_progress_and_stays_a_normal_result()
    {
        using var workspace = new TemporaryWorkspace();
        var tails = new List<string>();

        var result = await RunAsync(workspace, FailingCommand, tail =>
        {
            lock (tails)
            {
                tails.Add(tail);
            }
        });

        Assert.False(result.IsError, result.Content);
        Assert.DoesNotContain("exit: 0", result.Content, StringComparison.Ordinal);
        Assert.Contains("exit: ", result.Content, StringComparison.Ordinal);
        lock (tails)
        {
            Assert.NotEmpty(tails);
            Assert.Contains("before-failure", tails[^1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_command_that_fails_to_start_reports_no_progress_and_stays_an_error()
    {
        using var workspace = new TemporaryWorkspace();
        var calls = 0;

        var result = await RunAsync(workspace, "forge-no-such-program-xyz", _ => Interlocked.Increment(ref calls));

        Assert.True(result.IsError);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    /// <summary>The tool prints the wall time of the run, which differs between two runs of the same command.</summary>
    private static string StripDuration(string content) =>
        System.Text.RegularExpressions.Regex.Replace(content, @"duration: \d+ms", "duration: Xms");
}
