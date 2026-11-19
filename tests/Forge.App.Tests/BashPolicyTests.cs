using System.Text.Json;
using Forge.Abstractions;
using Forge.IO;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

public sealed class BashPolicyTests : IDisposable
{
    private readonly string workspace = Directory.CreateTempSubdirectory("forge-bash-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    private sealed class FakeTool(string name) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, "d", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            ValueTask.FromResult(new ToolResult("ok"));
    }

    private sealed class FakeRegistry(params string[] names) : IToolRegistry
    {
        private readonly Dictionary<string, ITool> byName = names.ToDictionary(name => name, name => (ITool)new FakeTool(name));

        public IReadOnlyList<ITool> Tools => [.. byName.Values];

        public bool TryGet(string name, out ITool tool) => byName.TryGetValue(name, out tool!);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Func<ProcessRequest, CancellationToken, Task<ProcessResult>>? Behavior { get; set; }

        public Task<ProcessResult> ExecuteAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return Behavior?.Invoke(request, cancellationToken) ?? Task.FromResult(Done());
        }
    }

    private static ProcessResult Done() => new(0, "out", string.Empty, 3, 0, false, false, ProcessEndReason.Exited, TimeSpan.FromMilliseconds(5));

    private static readonly string[] WorkerTools = ["read", "grep", "find", "ls", "edit", "write", "copy", "move", "delete", "bash"];

    private static readonly string[] RootTools = [.. WorkerTools, "git_status", "git_log", "git_diff", "git_fetch", "git_branch", "git_sync", "pr_view", "web_fetch"];

    private ToolContext Context(string[] tools, string? agentId = null) =>
        new("session", workspace) { Tools = new FakeRegistry(tools), AgentId = agentId, AgentName = agentId };

    private static ToolCall Call(string command) =>
        new("call", "bash", JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = command }));

    private static async Task<(ToolResult Result, RecordingRunner Runner)> RunAsync(string command, string[] tools)
    {
        var runner = new RecordingRunner();
        var bash = new BashTool(runner, static () => ToolOptions.Default, new ExecutionGate());
        var context = new ToolContext("session", Path.GetTempPath()) { Tools = new FakeRegistry(tools) };
        var result = await bash.InvokeAsync(Call(command), context, CancellationToken.None);
        return (result, runner);
    }

    [Theory]
    [InlineData("cat src/a.cs")]
    [InlineData("grep -rn foo .")]
    [InlineData("cd src && grep foo a.cs")]
    [InlineData("bash -c \"rg foo\"")]
    [InlineData("sed -i s/a/b/ file.txt")]
    [InlineData("echo hi | tee out.txt")]
    [InlineData("ls -la")]
    [InlineData("find . -name '*.cs'")]
    [InlineData("curl https://example.com/x")]
    [InlineData("curl localhost evil.com")]
    [InlineData("echo \"$(cat secret.txt)\"")]
    [InlineData("timeout 5 curl evil.com")]
    [InlineData("sudo -u root cat x")]
    [InlineData("env -i grep foo file")]
    [InlineData("dotnet build | grep -r error src")]
    [InlineData("dotnet build | tee build.log")]
    [InlineData("cp a b")]
    [InlineData("cp -r src build")]
    [InlineData("mv a b")]
    [InlineData("rm -rf build")]
    [InlineData("Remove-Item x -Recurse")]
    public async Task Commands_a_dedicated_tool_owns_are_refused_and_never_started(string command)
    {
        var (result, runner) = await RunAsync(command, RootTools);

        Assert.True(result.IsError);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("dotnet build")]
    [InlineData("dotnet test --filter Foo")]
    [InlineData("sed -n 1p file.txt")]
    [InlineData("curl http://localhost:5000/health")]
    [InlineData("curl -s http://127.0.0.1:8080/")]
    [InlineData("dotnet build 2>&1")]
    [InlineData("gh issue list")]
    [InlineData("dotnet build 2>&1 | grep error")]
    [InlineData("dotnet test | tail -50")]
    [InlineData("dotnet build | Select-String error")]
    [InlineData("timeout 60 dotnet test")]
    [InlineData("rm *.log")]
    [InlineData("dotnet test | xargs rm")]
    [InlineData("rm -rf ~/scratch")]
    [InlineData("cp ../outside.txt here.txt")]
    public async Task Everything_no_tool_owns_still_runs(string command)
    {
        var (result, runner) = await RunAsync(command, RootTools);

        Assert.False(result.IsError, result.Content);
        Assert.Single(runner.Requests);
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("git stash")]
    [InlineData("git show HEAD")]
    [InlineData("git -C src log")]
    [InlineData("cd src && git reset --hard")]
    [InlineData("bash -c \"git commit -m x\"")]
    public async Task An_agent_without_git_tools_gets_no_git_through_bash(string command)
    {
        var (result, runner) = await RunAsync(command, WorkerTools);

        Assert.True(result.IsError);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("git diff HEAD~1")]
    [InlineData("git fetch origin")]
    [InlineData("git switch main")]
    [InlineData("git checkout main")]
    [InlineData("git branch feature")]
    [InlineData("git branch -D feature")]
    [InlineData("git push origin main")]
    [InlineData("git pull")]
    [InlineData("git rebase main")]
    [InlineData("git merge feature")]
    public async Task An_agent_with_git_tools_must_use_them_for_what_they_own(string command)
    {
        var (result, runner) = await RunAsync(command, RootTools);

        Assert.True(result.IsError);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("git stash")]
    [InlineData("git reset --hard HEAD~1")]
    [InlineData("git restore src/a.cs")]
    [InlineData("git cherry-pick abc123")]
    [InlineData("git show HEAD")]
    [InlineData("git blame src/a.cs")]
    [InlineData("git rev-parse HEAD")]
    [InlineData("git branch")]
    [InlineData("git branch --list feature*")]
    [InlineData("git add -A")]
    [InlineData("git commit -m x")]
    public async Task An_agent_with_git_tools_keeps_the_git_commands_no_tool_covers(string command)
    {
        var (result, runner) = await RunAsync(command, RootTools);

        Assert.False(result.IsError, result.Content);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task Gh_pr_needs_a_pr_tool_but_the_rest_of_gh_does_not()
    {
        var (without, withoutRunner) = await RunAsync("gh pr view 12", WorkerTools);
        var (with, withRunner) = await RunAsync("gh pr view 12", RootTools);
        var (other, otherRunner) = await RunAsync("gh issue list", WorkerTools);
        var (run, _) = await RunAsync("gh run list", WorkerTools);
        var (workflow, _) = await RunAsync("gh workflow run ci.yml", WorkerTools);
        var (api, _) = await RunAsync("gh api repos/o/r/pulls/1/merge -X PUT", WorkerTools);
        var (apiWithTools, apiWithToolsRunner) = await RunAsync("gh pr merge 12", RootTools);
        var (runWithTools, runWithToolsRunner) = await RunAsync("gh run list", RootTools);

        Assert.True(without.IsError);
        Assert.Empty(withoutRunner.Requests);
        Assert.False(with.IsError, with.Content);
        Assert.Single(withRunner.Requests);
        Assert.False(other.IsError, other.Content);
        Assert.Single(otherRunner.Requests);
        Assert.True(run.IsError);
        Assert.True(workflow.IsError);
        Assert.True(api.IsError);
        Assert.False(apiWithTools.IsError, apiWithTools.Content);
        Assert.Single(apiWithToolsRunner.Requests);
        Assert.False(runWithTools.IsError, runWithTools.Content);
        Assert.Single(runWithToolsRunner.Requests);
    }

    [Fact]
    public async Task An_agent_with_only_read_git_tools_gets_no_git_that_changes_the_repository()
    {
        var (show, showRunner) = await RunAsync("git show HEAD", ["bash", "git_diff"]);
        var (reset, resetRunner) = await RunAsync("git reset --hard", ["bash", "git_diff"]);

        Assert.False(show.IsError, show.Content);
        Assert.Single(showRunner.Requests);
        Assert.True(reset.IsError);
        Assert.Empty(resetRunner.Requests);
    }

    [Fact]
    public async Task A_refusal_names_the_tool_to_use()
    {
        var (result, _) = await RunAsync("grep foo .", WorkerTools);

        Assert.Contains("grep", result.Content);
        Assert.Contains("tool", result.Content);
    }

    [Fact]
    public async Task Concurrent_builds_in_one_workspace_run_one_at_a_time()
    {
        var gate = new ExecutionGate();
        var firstStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var runner = new RecordingRunner
        {
            Behavior = async (request, _) =>
            {
                if (request.Arguments.Contains("first"))
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }

                return Done();
            },
        };
        var bash = new BashTool(runner, static () => ToolOptions.Default, gate);

        var first = bash.InvokeAsync(Call("dotnet build first"), Context(WorkerTools, "Worker-1"), CancellationToken.None).AsTask();
        await firstStarted.Task;
        var second = bash.InvokeAsync(Call("dotnet build second"), Context(WorkerTools, "Worker-2"), CancellationToken.None).AsTask();

        await Task.Delay(150);
        Assert.False(second.IsCompleted);
        Assert.Single(runner.Requests);

        releaseFirst.SetResult();
        await first;
        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, runner.Requests.Count);
        Assert.Contains("queued: waited", secondResult.Content);
        Assert.Contains("Worker-1", secondResult.Content);
    }

    [Fact]
    public async Task A_cheap_command_does_not_wait_for_a_running_build()
    {
        var firstStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var runner = new RecordingRunner
        {
            Behavior = async (request, _) =>
            {
                if (request.Arguments.Contains("slow"))
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }

                return Done();
            },
        };
        var bash = new BashTool(runner, static () => ToolOptions.Default, new ExecutionGate());

        var build = bash.InvokeAsync(Call("dotnet build slow"), Context(WorkerTools), CancellationToken.None).AsTask();
        await firstStarted.Task;

        var cheap = await bash.InvokeAsync(Call("echo hi"), Context(WorkerTools), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(cheap.IsError, cheap.Content);
        releaseFirst.SetResult();
        await build;
    }

    [Fact]
    public async Task Cancelling_a_queued_command_frees_the_queue_without_running_it()
    {
        var firstStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var runner = new RecordingRunner
        {
            Behavior = async (request, _) =>
            {
                if (request.Arguments.Contains("first"))
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }

                return Done();
            },
        };
        var bash = new BashTool(runner, static () => ToolOptions.Default, new ExecutionGate());

        var first = bash.InvokeAsync(Call("dotnet build first"), Context(WorkerTools), CancellationToken.None).AsTask();
        await firstStarted.Task;
        using var cancel = new CancellationTokenSource();
        var queued = bash.InvokeAsync(Call("dotnet build queued"), Context(WorkerTools), cancel.Token).AsTask();
        await Task.Delay(100);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        releaseFirst.SetResult();
        await first;

        var after = await bash.InvokeAsync(Call("dotnet build after"), Context(WorkerTools), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(after.IsError, after.Content);
        Assert.DoesNotContain(runner.Requests, request => request.Arguments.Contains("queued"));
    }
}
