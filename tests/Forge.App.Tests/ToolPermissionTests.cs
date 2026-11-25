using System.Runtime.CompilerServices;
using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Approval;
using Forge.App.Preferences;
using Forge.Core.Agents;
using Forge.Core.Tools;
using Forge.IO;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The tool permission contract: a read-only call always passes, a write or execution that stays inside the workspace passes,
/// one that names a path outside asks once (unless the user chose "authorize everything"), and an explicit rule beats both. The
/// file and shell tools answer for their own boundary, a plugin- or MCP-shaped tool falls back to its path-shaped arguments.
/// </summary>
public sealed class ToolPermissionTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("forge-permission-").FullName;
    private readonly string workspace;

    public ToolPermissionTests()
    {
        workspace = Path.Combine(root, "ws");
        Directory.CreateDirectory(workspace);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private sealed class PlainTool : ITool
    {
        public ToolDefinition Definition { get; } = new("plugin_tool", "d", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.Write;

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct) =>
            ValueTask.FromResult(new ToolResult("ok"));
    }

    /// <summary>Runs the queue's work inline: the queue, the handler and the cards need no Avalonia at all.</summary>
    private sealed class InlineDispatcher : IApprovalDispatcher
    {
        public bool CheckAccess() => true;

        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

        public void Post(Action action) => action();
    }

    private sealed class NeverRuns : IProcessRunner
    {
        public Task<ProcessResult> ExecuteAsync(ProcessRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no command runs in this test");
    }

    /// <summary>A write tool that records the context the loop handed it; its name is not one of Forge's, so the fallback scan judges it.</summary>
    private sealed class ContextProbeTool : ITool
    {
        public ToolDefinition Definition { get; } = new("probe_write", "probe", "{}");

        public ToolSideEffect SideEffect => ToolSideEffect.Write;

        public List<ToolContext> Contexts { get; } = [];

        public ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            Contexts.Add(context);
            return ValueTask.FromResult(new ToolResult("written"));
        }
    }

    /// <summary>Allows every card it is asked about and remembers the asks, so a missing card is visible as a count of 0.</summary>
    private sealed class AllowingHandler : IApprovalHandler
    {
        public List<ApprovalRequest> Requests { get; } = [];

        public ValueTask<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return ValueTask.FromResult(ApprovalDecision.Allow);
        }
    }

    /// <summary>Answers the first request with one tool call and every later one with a sentence, the way a provider does.</summary>
    private sealed class OneCallProvider(string arguments) : IChatProvider
    {
        private int calls;

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var index = Interlocked.Increment(ref calls);
            yield return new MessageCompleted(
                index == 1
                    ? new ChatMessage(MessageRole.Assistant, [new ToolCallBlock("c1", "probe_write", arguments)])
                    : new ChatMessage(MessageRole.Assistant, [new TextBlock("done")]),
                TokenUsage.Empty,
                StopReason.ToolUse);
        }
    }

    private static ToolCall Call(string arguments) => new("call", "tool", arguments);

    private static ToolCall Call(ITool tool, string arguments) => new("call", tool.Definition.Name, arguments);

    [Theory]
    [InlineData(ToolSideEffect.ReadOnly, ToolCallScope.OutsideWorkspace, ToolPermissionMode.Workspace, ApprovalMode.Allow)]
    [InlineData(ToolSideEffect.Write, ToolCallScope.InsideWorkspace, ToolPermissionMode.Workspace, ApprovalMode.Allow)]
    [InlineData(ToolSideEffect.Execute, ToolCallScope.InsideWorkspace, ToolPermissionMode.Workspace, ApprovalMode.Allow)]
    [InlineData(ToolSideEffect.Write, ToolCallScope.OutsideWorkspace, ToolPermissionMode.Workspace, ApprovalMode.Ask)]
    [InlineData(ToolSideEffect.Execute, ToolCallScope.OutsideWorkspace, ToolPermissionMode.Workspace, ApprovalMode.Ask)]
    [InlineData(ToolSideEffect.Write, ToolCallScope.OutsideWorkspace, ToolPermissionMode.All, ApprovalMode.Allow)]
    [InlineData(ToolSideEffect.Execute, ToolCallScope.InsideWorkspace, ToolPermissionMode.All, ApprovalMode.Allow)]
    public void The_policy_reads_the_mode_and_the_boundary(
        ToolSideEffect sideEffect,
        ToolCallScope scope,
        ToolPermissionMode mode,
        ApprovalMode expected)
    {
        var policy = new DefaultApprovalPolicy { Mode = () => mode };
        var request = new ApprovalRequest("write", "{}", sideEffect, workspace, Scope: scope);
        Assert.Equal(expected, policy.Evaluate(request));
    }

    [Fact]
    public void An_explicit_rule_beats_the_mode_and_the_boundary()
    {
        var rules = new Dictionary<string, ApprovalMode>(StringComparer.Ordinal)
        {
            ["extension_delete"] = ApprovalMode.Ask,
            ["todo"] = ApprovalMode.Allow,
        };
        var policy = new DefaultApprovalPolicy { ToolRules = rules, Mode = () => ToolPermissionMode.All };

        var deletion = new ApprovalRequest("extension_delete", "{}", ToolSideEffect.Write, workspace, Scope: ToolCallScope.InsideWorkspace);
        Assert.Equal(ApprovalMode.Ask, policy.Evaluate(deletion));
        Assert.Equal(
            ApprovalMode.Allow,
            policy.Evaluate(new ApprovalRequest("todo", "{}", ToolSideEffect.Write, workspace, Scope: ToolCallScope.OutsideWorkspace)));
    }

    [Fact]
    public void The_file_tools_report_their_own_boundary()
    {
        var write = new WriteTool();
        var inside = Path.Combine(workspace, "sub", "a.txt");
        var outside = Path.Combine(root, "outside.txt");

        Assert.Equal(ToolCallScope.InsideWorkspace, write.ScopeOf(Call(write, """{"path":"sub/a.txt","content":"x"}"""), workspace));
        Assert.Equal(ToolCallScope.InsideWorkspace, write.ScopeOf(Call(write, JsonSerializer.Serialize(new { path = inside, content = "x" })), workspace));
        Assert.Equal(ToolCallScope.OutsideWorkspace, write.ScopeOf(Call(write, """{"path":"../escape.txt","content":"x"}"""), workspace));
        Assert.Equal(ToolCallScope.OutsideWorkspace, write.ScopeOf(Call(write, JsonSerializer.Serialize(new { path = outside, content = "x" })), workspace));

        // A glob is judged by the directory it starts from.
        var copy = new CopyTool();
        Assert.Equal(ToolCallScope.InsideWorkspace, copy.ScopeOf(Call(copy, """{"from":"sub/*.txt","to":"dst"}"""), workspace));
        Assert.Equal(ToolCallScope.OutsideWorkspace, copy.ScopeOf(Call(copy, """{"from":"../outside/*.txt","to":"dst"}"""), workspace));

        var bash = new BashTool(new NeverRuns());
        Assert.Equal(ToolCallScope.InsideWorkspace, bash.ScopeOf(Call(bash, """{"command":"git status && dotnet build src/Forge.IO"}"""), workspace));
        Assert.Equal(ToolCallScope.OutsideWorkspace, bash.ScopeOf(Call(bash, """{"command":"git status","workdir":"../elsewhere"}"""), workspace));
        Assert.Equal(ToolCallScope.OutsideWorkspace, bash.ScopeOf(Call(bash, """{"command":"cp ../outside/a.txt b.txt"}"""), workspace));
        Assert.Equal(ToolCallScope.OutsideWorkspace, bash.ScopeOf(Call(bash, """{"command":"dotnet build --output=/tmp/out"}"""), workspace));
        Assert.Equal(ToolCallScope.InsideWorkspace, bash.ScopeOf(Call(bash, """{"command":"dotnet build -o out --configuration Release"}"""), workspace));
    }

    [Fact]
    public void A_tool_without_a_boundary_of_its_own_is_judged_by_its_path_shaped_arguments()
    {
        var tool = new PlainTool();

        Assert.Equal(ToolCallScope.OutsideWorkspace, ToolScopes.Of(tool, Call("""{"file":"../../x"}"""), workspace));
        Assert.Equal(ToolCallScope.OutsideWorkspace, ToolScopes.Of(tool, Call("""{"target":"/etc/passwd"}"""), workspace));
        Assert.Equal(ToolCallScope.InsideWorkspace, ToolScopes.Of(tool, Call("""{"path":"sub/a.txt"}"""), workspace));
        Assert.Equal(ToolCallScope.InsideWorkspace, ToolScopes.Of(tool, Call("""{"query":"../x"}"""), workspace));
        Assert.Equal(ToolCallScope.InsideWorkspace, ToolScopes.Of(tool, Call("""{"url":"https://example.com/a?b=1"}"""), workspace));
        Assert.Equal(ToolCallScope.OutsideWorkspace, ToolScopes.Of(tool, Call("""{"paths":["D:/outside/x","/etc/y"]}"""), workspace));
        Assert.Equal(ToolCallScope.OutsideWorkspace, ToolScopes.Of(tool, Call("""{"src":"../x","dst":"out"}"""), workspace));
    }

    [Fact]
    public async Task A_write_outside_the_workspace_runs_only_for_an_allowed_call()
    {
        var target = Path.Combine(root, "outside.txt");
        var arguments = JsonSerializer.Serialize(new { path = target, content = "hi" });

        var denied = await new WriteTool().InvokeAsync(
            new ToolCall("call", "write", arguments),
            new ToolContext("session", workspace),
            TestContext.Current.CancellationToken);
        Assert.True(denied.IsError);
        Assert.Contains("outside the workspace", denied.Content, StringComparison.Ordinal);
        Assert.False(File.Exists(target));

        var allowed = await new WriteTool().InvokeAsync(
            new ToolCall("call", "write", arguments),
            new ToolContext("session", workspace) { OutOfWorkspaceAllowed = true },
            TestContext.Current.CancellationToken);
        Assert.False(allowed.IsError, allowed.Content);
        Assert.Equal("hi", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_delete_outside_the_workspace_runs_only_for_an_allowed_call()
    {
        var target = Path.Combine(root, "gone.txt");
        await File.WriteAllTextAsync(target, "x", TestContext.Current.CancellationToken);
        var arguments = """{"path":"../gone.txt"}""";

        var denied = await new DeleteTool().InvokeAsync(
            new ToolCall("call", "delete", arguments),
            new ToolContext("session", workspace),
            TestContext.Current.CancellationToken);
        Assert.True(denied.IsError);
        Assert.True(File.Exists(target));

        var allowed = await new DeleteTool().InvokeAsync(
            new ToolCall("call", "delete", arguments),
            new ToolContext("session", workspace) { OutOfWorkspaceAllowed = true },
            TestContext.Current.CancellationToken);
        Assert.False(allowed.IsError, allowed.Content);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void The_sessions_own_rules_keep_the_deletion_confirmation_in_both_modes()
    {
        var workspaceMode = SessionRuntime.CreatePolicy(() => ToolPermissionMode.Workspace);
        var allMode = SessionRuntime.CreatePolicy(() => ToolPermissionMode.All);
        var deletion = new ApprovalRequest(
            ExtensionToolNames.Delete,
            "{}",
            ToolSideEffect.Write,
            workspace,
            ToolCallScope.InsideWorkspace);

        Assert.Equal(ApprovalMode.Ask, workspaceMode.Evaluate(deletion));
        Assert.Equal(ApprovalMode.Ask, allMode.Evaluate(deletion));
        Assert.Equal(
            ApprovalMode.Allow,
            workspaceMode.Evaluate(new ApprovalRequest("todo", "{}", ToolSideEffect.Write, workspace, ToolCallScope.OutsideWorkspace)));
    }

    [Theory]
    [InlineData("""{"path":"inside.txt"}""", false)]
    [InlineData("""{"path":"../outside.txt"}""", true)]
    public async Task The_loop_hands_the_boundary_marker_only_to_an_allowed_outside_call(string arguments, bool outside)
    {
        var tool = new ContextProbeTool();
        var handler = new AllowingHandler();
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => new OneCallProvider(arguments),
            Tools = [tool],
            Layers = new PromptLayers("sys", null, null),
            ApprovalHandler = handler,
        });
        var root = host.CreateRoot("session-boundary", workspace);

        await foreach (var _ in host.RunAsync(root, "write it", TestContext.Current.CancellationToken))
        {
        }

        var context = Assert.Single(tool.Contexts);
        Assert.Equal(outside, context.OutOfWorkspaceAllowed);
        Assert.Equal(outside ? 1 : 0, handler.Requests.Count);
    }

    [Fact]
    public async Task The_read_tools_reach_outside_the_workspace_and_report_absolute_paths()
    {
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "settings.json");
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(file, """{"promptCacheTtl":"5m"}""", token);

        // The tools report the real path (links resolved); the temporary directory may itself sit under one (`/var` on macOS).
        var shown = WorkspacePath.ResolveRealPath(Path.GetFullPath(file));

        var grep = await new GrepTool().InvokeAsync(
            new ToolCall("call", "grep", JsonSerializer.Serialize(new { pattern = "promptCacheTtl", path = file })),
            new ToolContext("session", workspace),
            token);
        Assert.False(grep.IsError, grep.Content);
        Assert.Equal($"{shown}:1:{{\"promptCacheTtl\":\"5m\"}}", grep.Content);

        var ls = await new ListTool().InvokeAsync(
            new ToolCall("call", "ls", JsonSerializer.Serialize(new { path = outside })),
            new ToolContext("session", workspace),
            token);
        Assert.False(ls.IsError, ls.Content);
        Assert.Equal("f " + shown, ls.Content);

        var find = await new FindTool().InvokeAsync(
            new ToolCall("call", "find", JsonSerializer.Serialize(new { pattern = "*.json", path = outside })),
            new ToolContext("session", workspace),
            token);
        Assert.False(find.IsError, find.Content);
        Assert.Equal(shown, find.Content);
    }

    [Fact]
    public async Task A_remembered_rule_never_answers_a_call_that_leaves_the_workspace()
    {
        var session = new SessionInfo("session-rules", workspace, "Rules", DateTimeOffset.UnixEpoch);
        var queue = new ApprovalQueue(new InlineDispatcher());
        await using var rules = new ApprovalRules(Path.Combine(root, "approval-rules.json"), debounceMilliseconds: 0);
        Assert.True(rules.Allow(ApprovalRuleKey.Global("write", ToolSideEffect.Write)));
        using var handler = new SessionApprovalHandler(queue, rules, session);

        var inside = new ApprovalRequest("write", "{}", ToolSideEffect.Write, workspace, ToolCallScope.InsideWorkspace);
        Assert.Equal(ApprovalDecision.Allow, await handler.RequestAsync(inside, CancellationToken.None));

        // The same call outside must reach the card path. A token that is already cancelled makes that observable: the rule
        // answers before the token is ever looked at, the queue does not.
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var outside = new ApprovalRequest("write", "{}", ToolSideEffect.Write, workspace, ToolCallScope.OutsideWorkspace);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await handler.RequestAsync(outside, cancelled.Token));
        Assert.Empty(queue.Items);
    }

    [Fact]
    public void A_card_for_an_outside_call_offers_neither_a_remembered_rule_nor_a_run_grant()
    {
        var dispatcher = new InlineDispatcher();
        using var outside = new ToolApprovalCardViewModel(
            new ToolApprovalCardRequest(
                "session", "Title", null, "write", ToolSideEffect.Write, workspace, "{}", RunKey: "run", OutsideWorkspace: true),
            dispatcher);
        using var inside = new ToolApprovalCardViewModel(
            new ToolApprovalCardRequest(
                "session", "Title", null, "write", ToolSideEffect.Write, workspace, "{}", RunKey: "run"),
            dispatcher);

        Assert.False(outside.CanRemember);
        Assert.False(outside.HasRunScope);
        Assert.True(inside.CanRemember);
        Assert.True(inside.HasRunScope);
    }

    [Fact]
    public void The_bash_boundary_sees_paths_inside_the_arguments()
    {
        var bash = new BashTool(new NeverRuns());

        // An interpreter payload, an assignment's right-hand side and a path glued to a switch all name a path outside.
        Assert.Equal(
            ToolCallScope.OutsideWorkspace,
            bash.ScopeOf(Call(bash, """{"command":"python -c \"open('/etc/x','w').write('y')\""}"""), workspace));
        Assert.Equal(ToolCallScope.OutsideWorkspace, bash.ScopeOf(Call(bash, """{"command":"OUT=/tmp/out some-tool"}"""), workspace));
        Assert.Equal(ToolCallScope.OutsideWorkspace, bash.ScopeOf(Call(bash, """{"command":"cc -I/abs/include x.c"}"""), workspace));
        Assert.Equal(ToolCallScope.InsideWorkspace, bash.ScopeOf(Call(bash, """{"command":"dotnet build src/Forge.IO -c Release"}"""), workspace));
    }

    [Fact]
    public async Task A_home_relative_path_is_refused_rather_than_treated_as_a_workspace_path()
    {
        var write = new WriteTool();
        Assert.Equal(ToolCallScope.InsideWorkspace, write.ScopeOf(Call(write, """{"path":"~/notes.txt","content":"x"}"""), workspace));

        var result = await write.InvokeAsync(
            new ToolCall("call", "write", """{"path":"~/notes.txt","content":"x"}"""),
            new ToolContext("session", workspace),
            TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("is not expanded", result.Content, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(workspace, "~")));
    }

    [Fact]
    public async Task An_allowed_outside_call_still_cannot_delete_a_version_control_directory()
    {
        var repository = Path.Combine(root, "other");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        var config = Path.Combine(repository, ".git", "config");
        await File.WriteAllTextAsync(config, "x", TestContext.Current.CancellationToken);

        var result = await new DeleteTool().InvokeAsync(
            new ToolCall("call", "delete", JsonSerializer.Serialize(new { path = Path.Combine(repository, ".git") })),
            new ToolContext("session", workspace) { OutOfWorkspaceAllowed = true },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Contains("the .git directory is not deletable", result.Content, StringComparison.Ordinal);
        Assert.True(File.Exists(config));
    }

    [Fact]
    public async Task An_allowed_outside_copy_writes_the_destination()
    {
        await File.WriteAllTextAsync(Path.Combine(workspace, "a.txt"), "a", TestContext.Current.CancellationToken);
        var destination = Path.Combine(root, "copied.txt");

        var result = await new CopyTool().InvokeAsync(
            new ToolCall("call", "copy", JsonSerializer.Serialize(new { from = "a.txt", to = destination })),
            new ToolContext("session", workspace) { OutOfWorkspaceAllowed = true },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError, result.Content);
        Assert.Equal("a", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_argument_scan_fails_closed_on_arguments_it_cannot_read()
    {
        var tool = new PlainTool();

        Assert.Equal(ToolCallScope.OutsideWorkspace, ToolScopes.Of(tool, Call("{not json"), workspace));
        Assert.Equal(ToolCallScope.OutsideWorkspace, ToolScopes.Of(tool, Call("[1,2]"), workspace));
    }

    [Fact]
    public async Task The_permission_mode_round_trips_and_an_unknown_token_falls_back_to_the_workspace()
    {
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, """{"preferences":{"toolsPermission":"all"}}""", TestContext.Current.CancellationToken);

        await using (var store = new PreferencesStore(settings))
        {
            await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(AppPreferences.ToolsPermissionAll, store.Current.ToolsPermission);

            store.Current.ToolsPermission = AppPreferences.ToolsPermissionWorkspace;
            await store.SaveAsync(TestContext.Current.CancellationToken);
        }

        await using (var reloaded = new PreferencesStore(settings))
        {
            await reloaded.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(AppPreferences.ToolsPermissionWorkspace, reloaded.Current.ToolsPermission);
        }

        await File.WriteAllTextAsync(settings, """{"preferences":{"toolsPermission":"nonsense"}}""", TestContext.Current.CancellationToken);
        await using var fallback = new PreferencesStore(settings);
        await fallback.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AppPreferences.DefaultToolsPermission, fallback.Current.ToolsPermission);
    }
}
