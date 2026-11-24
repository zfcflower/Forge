using System.Text.Json;
using Forge.Abstractions;
using Forge.IO;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Two things a command's answer must not do: repeat the same warning a hundred times (MSBuild's locked-file retry does),
/// or start a build that cannot finish because its own output is open. Both are pinned here end to end through the tool.
/// </summary>
public sealed class BashOutputTests : IDisposable
{
    private readonly string workspace = Directory.CreateTempSubdirectory("forge-bash-output-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    private sealed class RecordingRunner(ProcessResult result) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> ExecuteAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }

    private static ToolCall Call(string command) =>
        new("call", "bash", JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = command }));

    private static ProcessResult Result(string stdout) =>
        new(0, stdout, string.Empty, stdout.Length, 0, false, false, ProcessEndReason.Exited, TimeSpan.FromMilliseconds(5));

    private async Task<(ToolResult Result, RecordingRunner Runner)> RunAsync(string command, ProcessResult result)
    {
        var runner = new RecordingRunner(result);
        var bash = new BashTool(runner, static () => ToolOptions.Default, new ExecutionGate());
        var context = new ToolContext("session", workspace);
        return (await bash.InvokeAsync(Call(command), context, CancellationToken.None), runner);
    }

    [Fact]
    public async Task A_long_run_of_lines_that_differ_only_in_digits_folds_to_one_line_and_a_count()
    {
        var warnings = string.Join('\n', Enumerable.Range(1, 12).Select(retry => $"warning MSB3026: retry {retry} for Forge.IO.dll"));

        var (result, _) = await RunAsync("dotnet build Demo.csproj", Result(warnings + "\n"));

        var content = result.Content;
        Assert.Contains("repeated lines folded", content);
        Assert.Contains("warning MSB3026: retry 1 for Forge.IO.dll  [×11 more lines, same message]", content);
        Assert.DoesNotContain("retry 2", content);
    }

    [Fact]
    public async Task A_short_run_and_a_different_line_stay_verbatim()
    {
        var text = "warning MSB3026: retry 1\nwarning MSB3026: retry 1\nwarning MSB3026: retry 1\nwarning MSB3026: retry 1\nbuild failed\n";

        var (result, _) = await RunAsync("dotnet build Demo.csproj", Result(text));

        Assert.Contains(
            $"--- stdout ({text.Length} bytes) ---\n"
            + "warning MSB3026: retry 1\nwarning MSB3026: retry 1\nwarning MSB3026: retry 1\nwarning MSB3026: retry 1\nbuild failed\n",
            result.Content);
        Assert.DoesNotContain("more lines", result.Content);
    }

    [Fact]
    public async Task A_held_output_assembly_fails_the_build_before_anything_runs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var project = Path.Combine(workspace, "Demo.csproj");
        File.WriteAllText(project, "<Project />");
        var assembly = Path.Combine(workspace, "bin", "Debug", "net11.0", "Demo.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        File.WriteAllText(assembly, "x");

        using var held = new FileStream(assembly, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var (result, runner) = await RunAsync("dotnet build Demo.csproj", Result("should not run\n"));

        Assert.True(result.IsError);
        Assert.Equal(
            $"bash: not started: '{assembly}' is held open by another process (a running build output is the usual cause); "
            + "close it or build a copy.",
            result.Content);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task A_build_whose_output_is_free_still_runs()
    {
        var project = Path.Combine(workspace, "Demo.csproj");
        File.WriteAllText(project, "<Project />");
        var assembly = Path.Combine(workspace, "bin", "Debug", "net11.0", "Demo.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(assembly)!);
        File.WriteAllText(assembly, "x");

        var (result, runner) = await RunAsync("dotnet build Demo.csproj", Result("done\n"));

        Assert.False(result.IsError, result.Content);
        Assert.Single(runner.Requests);
    }
}
