using Forge.IO.Tools;
using Xunit;

namespace Forge.IO.Tests;

/// <summary>
/// The <c>bash</c> tool through <see cref="Forge.Abstractions.ITool.InvokeAsync"/>: it picks direct mode for a command line
/// that is just a program plus arguments and the platform shell for anything with shell syntax, reports the mode and the
/// exit code it actually saw, keeps a non-zero exit a normal answer, refuses a working directory outside the workspace
/// before running anything, and stops a command at its timeout with the output captured so far. The process contract
/// itself (stream caps, process-tree kill, stdin) is covered by <see cref="ProcessRunnerTests"/>; this file covers the tool
/// layer that decides those things.
/// </summary>
public class BashToolTests
{
    private static BashTool Tool(ToolOptions? options = null)
    {
        var bounds = options ?? ToolOptions.Default;
        return new BashTool(new ProcessRunner(), () => bounds);
    }

    [Fact]
    public async Task A_simple_command_runs_direct_and_reports_its_output()
    {
        using var workspace = new TemporaryWorkspace();
        // `echo` is a shell builtin, and Windows has no echo.exe on a plain runner's PATH, so the Windows direct case runs
        // a real executable whose binary is always there. The byte count differs because cmd ends the line with CRLF.
        var command = OperatingSystem.IsWindows() ? "cmd /c echo hi" : "echo hi";
        var bytes = OperatingSystem.IsWindows() ? 4 : 3;

        var result = await workspace.InvokeSuccessAsync(Tool(), TemporaryWorkspace.Json(new { command }));

        Assert.Contains("mode: direct", result.Content, StringComparison.Ordinal);
        Assert.Contains("exit: 0", result.Content, StringComparison.Ordinal);
        Assert.Contains($"--- stdout ({bytes} bytes) ---", result.Content, StringComparison.Ordinal);
        Assert.Contains("hi", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_line_with_shell_syntax_runs_through_the_platform_shell()
    {
        using var workspace = new TemporaryWorkspace();

        var result = await workspace.InvokeSuccessAsync(
            Tool(),
            TemporaryWorkspace.Json(new { command = "echo hi && echo there" }));

        Assert.Contains("mode: script (", result.Content, StringComparison.Ordinal);
        var dialect = OperatingSystem.IsWindows() ? new[] { "pwsh", "cmd" } : ["bash", "sh"];
        Assert.Contains(dialect, name => result.Content.Contains(name + ")", StringComparison.Ordinal));
        Assert.Contains("hi", result.Content, StringComparison.Ordinal);
        Assert.Contains("there", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_working_directory_outside_the_workspace_is_refused_and_nothing_runs()
    {
        using var workspace = new TemporaryWorkspace();
        using var outside = new TestDirectory();

        var result = await workspace.InvokeAsync(
            Tool(),
            TemporaryWorkspace.Json(new { command = "echo x> marker.txt", workdir = outside.Path }));

        Assert.True(result.IsError);
        Assert.Contains("outside the workspace", result.Content, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(outside.Path, "marker.txt")));
        Assert.False(File.Exists(workspace.Full("marker.txt")));
    }

    [Fact]
    public async Task A_directory_link_that_leaves_the_workspace_is_refused_as_a_working_directory()
    {
        using var workspace = new TemporaryWorkspace();
        using var outside = new TestDirectory();
        var link = Path.Combine(workspace.Root, "escape");
        if (!DirectoryLinks.TryCreate(link, outside.Path))
        {
            return;
        }

        var result = await workspace.InvokeAsync(
            Tool(),
            TemporaryWorkspace.Json(new { command = "echo x> marker.txt", workdir = "escape" }));

        Assert.True(result.IsError);
        Assert.False(File.Exists(Path.Combine(outside.Path, "marker.txt")));
    }

    [Fact]
    public async Task A_command_that_cannot_start_is_an_error_result()
    {
        using var workspace = new TemporaryWorkspace();

        var result = await workspace.InvokeAsync(
            Tool(),
            TemporaryWorkspace.Json(new { command = "forge-no-such-program-xyz --flag" }));

        Assert.True(result.IsError);
        Assert.Contains("forge-no-such-program-xyz", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_exit_code_is_reported_and_is_not_an_error()
    {
        using var workspace = new TemporaryWorkspace();
        var command = OperatingSystem.IsWindows() ? "cmd /c exit 3" : "sh -c \"exit 3\"";

        var result = await workspace.InvokeSuccessAsync(Tool(), TemporaryWorkspace.Json(new { command }));

        Assert.Contains("exit: 3", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_that_runs_too_long_is_stopped_and_says_so()
    {
        using var workspace = new TemporaryWorkspace();
        var command = OperatingSystem.IsWindows() ? "cmd /c ping -n 20 127.0.0.1" : "sleep 20";

        var result = await workspace.InvokeSuccessAsync(
            Tool(),
            TemporaryWorkspace.Json(new { command, timeout_seconds = 1 }));

        Assert.Contains("exit: timed out after 1s", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_above_the_capture_cap_is_marked_truncated()
    {
        using var workspace = new TemporaryWorkspace();
        // A pipe (and a script block on Windows) makes this a script-mode command in both dialects; the 20 KB it writes is
        // well past the 1 KiB cap, so the run has to keep reading and say that it cut the answer.
        var command = OperatingSystem.IsWindows()
            ? "1..500 | ForEach-Object { \"0123456789012345678901234567890123456789\" }"
            : "yes 0123456789012345678901234567890123456789 | head -n 500";

        var result = await workspace.InvokeSuccessAsync(
            Tool(ToolOptions.Default with { ShellCaptureBytes = 1024 }),
            TemporaryWorkspace.Json(new { command }));

        Assert.Contains("--- stdout (", result.Content, StringComparison.Ordinal);
        Assert.Contains("bytes, truncated) ---", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_child_sees_the_agent_environment_and_not_the_harness_git_variables()
    {
        using var workspace = new TemporaryWorkspace();
        var command = OperatingSystem.IsWindows()
            ? "cmd /c echo NC=%NO_COLOR% TD=%TERM% GP=%GIT_TERMINAL_PROMPT% GD=[%GIT_DIR%]"
            : "sh -c 'echo NC=$NO_COLOR TD=$TERM GP=$GIT_TERMINAL_PROMPT GD=[$GIT_DIR]'";

        var result = await workspace.InvokeSuccessAsync(Tool(), TemporaryWorkspace.Json(new { command }));

        Assert.Contains("NC=1", result.Content, StringComparison.Ordinal);
        Assert.Contains("TD=dumb", result.Content, StringComparison.Ordinal);
        Assert.Contains("GP=0", result.Content, StringComparison.Ordinal);
        // git must not inherit a repository location: cmd leaves an undefined %NAME% literally, a POSIX shell expands it to
        // nothing, so each dialect has its own way of saying "the variable was not there".
        Assert.Contains(OperatingSystem.IsWindows() ? "GD=[%GIT_DIR%]" : "GD=[]", result.Content, StringComparison.Ordinal);
    }
}
