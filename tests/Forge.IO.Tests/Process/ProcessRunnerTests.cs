using System.Diagnostics;
using Xunit;
using static Forge.IO.Tests.TestSupport;

namespace Forge.IO.Tests;

/// <summary>
/// The process runner contract: exit code, two independently capped streams whose byte counts survive the cap, a child
/// that keeps writing past the cap and still exits, a timeout that reports what it captured, cancellation that throws
/// instead of returning, start failures as results, the environment the child sees, BOM handling, and a closed stdin.
/// </summary>
public class ProcessRunnerTests
{
    private const int EightMebibytes = 8 * 1024 * 1024;

    [Fact]
    public async Task The_exit_code_of_the_child_is_reported()
    {
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var request = ChildProcess.Request(ChildProcess.ExitCodeMode, workspace.Root, count: 3, timeout: TestLimit);

        var result = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(ProcessEndReason.Exited, result.EndReason);
        Assert.False(result.Success);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task Arguments_arrive_as_a_vector_with_their_spaces_quotes_and_backslashes_intact()
    {
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var request = ChildProcess.Request(ChildProcess.PrintArgumentsMode, workspace.Root, timeout: TestLimit) with
        {
            Arguments = [ChildProcess.AssemblyPath, "a b", "c\"d", "e\\f", string.Empty],
        };

        var result = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("<a b>\n<c\"d>\n<e\\f>\n<>\n", result.Stdout.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Each_stream_has_its_own_cap_and_its_own_byte_count()
    {
        // Standard output is capped at 4 KiB and standard error at 64 KiB, so the same run has one truncated stream and
        // one that fits: a shared cap or a shared counter would make these two answers identical.
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var request = ChildProcess.Request(
            ChildProcess.WriteBothMode,
            workspace.Root,
            count: 200 * 1024,
            bytes: 10 * 1024,
            timeout: TestLimit,
            maxStdoutBytes: 4 * 1024,
            maxStderrBytes: 64 * 1024);

        var result = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(200L * 1024, result.StdoutBytes);
        Assert.True(result.StdoutTruncated);
        Assert.Equal(4 * 1024, result.Stdout.Length);
        Assert.Equal(10L * 1024, result.StderrBytes);
        Assert.False(result.StderrTruncated);
        Assert.Equal(10 * 1024, result.Stderr.Length);
        Assert.Equal(ProcessEndReason.Exited, result.EndReason);
    }

    [Fact]
    public async Task A_child_that_writes_past_the_cap_keeps_reading_its_output_and_exits_normally()
    {
        // Eight mebibytes into a 64 KiB cap: an implementation that stopped reading at the cap would block the child on a
        // full pipe and report a timeout instead of the exit it made.
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var request = ChildProcess.Request(
            ChildProcess.WriteStdoutMode,
            workspace.Root,
            count: EightMebibytes,
            timeout: TimeSpan.FromSeconds(60),
            maxStdoutBytes: 64 * 1024);

        var result = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProcessEndReason.Exited, result.EndReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal((long)EightMebibytes, result.StdoutBytes);
        Assert.True(result.StdoutTruncated);
        Assert.Equal(64 * 1024, result.Stdout.Length);
        Assert.True(result.Duration < TestLimit, $"the run took {result.Duration.TotalSeconds:F1} s");
    }

    [Fact]
    public async Task A_timeout_reports_the_end_reason_and_the_output_captured_before_the_kill()
    {
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var timeout = TimeSpan.FromMilliseconds(500);
        var request = ChildProcess.Request(ChildProcess.SleepAfterOutputMode, workspace.Root, timeout: timeout);

        var result = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProcessEndReason.TimedOut, result.EndReason);
        Assert.Equal("started\n", result.Stdout);
        Assert.True(result.Duration >= timeout, $"the run ended after {result.Duration.TotalMilliseconds:F0} ms, before its timeout");
        Assert.True(result.Duration < TestLimit, $"the run took {result.Duration.TotalSeconds:F1} s");
    }

    [Fact]
    public async Task Cancellation_throws_OperationCanceledException_and_nothing_else()
    {
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var request = ChildProcess.Request(
            ChildProcess.SleepAfterOutputMode,
            workspace.Root,
            timeout: TimeSpan.FromSeconds(60));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var exception = await Record.ExceptionAsync(() => runner.ExecuteAsync(request, cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.IsNotType<AggregateException>(exception);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_program_that_cannot_be_started_is_a_result_with_the_start_failure_code(bool absolutePath)
    {
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var missing = absolutePath ? Path.Combine(workspace.Root, "no-such-program.exe") : "forge-no-such-command-xyz";
        var request = new ProcessRequest
        {
            FileName = missing,
            Arguments = [],
            WorkingDirectory = workspace.Root,
            Timeout = TestLimit,
        };

        var result = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProcessRunner.StartFailedExitCode, result.ExitCode);
        Assert.Equal(ProcessEndReason.Exited, result.EndReason);
        Assert.False(result.Success);
        Assert.Contains(missing, result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0L, result.StdoutBytes);
    }

    [Fact]
    public async Task A_missing_working_directory_is_a_start_failure()
    {
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var request = ChildProcess.Request(ChildProcess.ExitCodeMode, Path.Combine(workspace.Root, "missing"), timeout: TestLimit);

        var result = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProcessRunner.StartFailedExitCode, result.ExitCode);
        Assert.Contains("Could not start", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_environment_can_be_overridden_and_a_variable_removed()
    {
        const string Name = "FORGE_IO_TEST_CHILD_ENV_VALUE";
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var previous = Environment.GetEnvironmentVariable(Name);
        Environment.SetEnvironmentVariable(Name, "parent-value");
        try
        {
            var overridden = await runner.ExecuteAsync(
                ChildProcess.Request(
                    ChildProcess.PrintEnvironmentMode,
                    workspace.Root,
                    path: Name,
                    timeout: TestLimit,
                    environment: new Dictionary<string, string?> { [Name] = "child-value" }),
                CancellationToken.None);
            var removed = await runner.ExecuteAsync(
                ChildProcess.Request(
                    ChildProcess.PrintEnvironmentMode,
                    workspace.Root,
                    path: Name,
                    timeout: TestLimit,
                    environment: new Dictionary<string, string?> { [Name] = null }),
                CancellationToken.None);
            var inherited = await runner.ExecuteAsync(
                ChildProcess.Request(ChildProcess.PrintEnvironmentMode, workspace.Root, path: Name, timeout: TestLimit),
                CancellationToken.None);

            Assert.Equal("child-value\n", overridden.Stdout);
            Assert.Equal("<unset>\n", removed.Stdout);
            Assert.Equal("parent-value\n", inherited.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, previous);
        }
    }

    [Fact]
    public async Task Repository_location_variables_are_not_inherited_by_the_child()
    {
        const string Name = "GIT_DIR";
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var previous = Environment.GetEnvironmentVariable(Name);
        Environment.SetEnvironmentVariable(Name, workspace.Root);
        try
        {
            var result = await runner.ExecuteAsync(
                ChildProcess.Request(ChildProcess.PrintEnvironmentMode, workspace.Root, path: Name, timeout: TestLimit),
                CancellationToken.None);

            Assert.Equal("<unset>\n", result.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, previous);
        }
    }

    [Fact]
    public async Task A_utf8_bom_is_stripped_from_the_text_but_counted_in_the_byte_total()
    {
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var request = ChildProcess.Request(ChildProcess.WriteBomMode, workspace.Root, timeout: TestLimit);

        var result = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal("hello\n", result.Stdout);
        Assert.Equal(9L, result.StdoutBytes);
        Assert.False(result.StdoutTruncated);
    }

    [Fact]
    public async Task Standard_input_is_closed_so_a_reader_ends_instead_of_waiting_for_the_timeout()
    {
        using var workspace = new TemporaryWorkspace();
        var runner = new ProcessRunner();
        var request = ChildProcess.Request(ChildProcess.ReadStdinMode, workspace.Root, timeout: TimeSpan.FromSeconds(30));

        var result = await runner.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProcessEndReason.Exited, result.EndReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("eof:0\n", result.Stdout);
        Assert.True(
            result.Duration < TimeSpan.FromSeconds(5),
            $"a reader of standard input took {result.Duration.TotalSeconds:F2} s, so the pipe was not closed");
    }
}
