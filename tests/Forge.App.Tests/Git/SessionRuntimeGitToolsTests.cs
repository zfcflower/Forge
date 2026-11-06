using System.Reflection;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.Core.Agents;
using Forge.HostClient;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins the agent-runtime git surface: <see cref="SessionRuntime.BuildTools"/> exposes the six git tools
/// (git_status, git_log, git_diff, git_fetch, git_branch, git_sync) when <see cref="AgentRuntimeServices.Git"/>
/// is set and none when it is null; a host without registered tools adds no <c>run_code</c> entry point even
/// though the git tools are registered (the script registry is built from host tools only, never from the
/// git surface); and with the app's approval policy (<see cref="DefaultApprovalPolicy"/> as configured in
/// <see cref="SessionRuntime"/>, no handler) the four read-only tools evaluate to <see cref="ApprovalMode.Allow"/>
/// while git_branch/git_sync do not.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionRuntimeGitToolsTests
{
    private static readonly string[] ExpectedGitTools =
    [
        GitTools.StatusName,
        GitTools.LogName,
        GitTools.DiffName,
        GitTools.FetchName,
        GitTools.BranchName,
        GitTools.SyncName,
    ];

    [Fact]
    public void BuildTools_includes_exactly_the_six_git_tools_when_Git_is_set()
    {
        var runtime = NewRuntime(
            new GitTools(new GitCli(), new GitToolSettings(), new GitReviewLedger()));

        var names = runtime.BuildTools().Select(static tool => tool.Definition.Name).ToList();

        // "extension-host tools plus run_code, then the git tools": with no host client the surface is
        // exactly GitTools.All, in registration order (status, log, diff, fetch, branch, sync).
        Assert.Equal(ExpectedGitTools, names);
    }

    [Fact]
    public void BuildTools_exposes_no_git_tools_when_Git_is_null()
    {
        var runtime = NewRuntime(null);

        var names = runtime.BuildTools().Select(static tool => tool.Definition.Name).ToList();

        Assert.Empty(names);
        Assert.DoesNotContain(GitTools.StatusName, names);
        Assert.DoesNotContain(GitTools.BranchName, names);
        Assert.DoesNotContain(GitTools.SyncName, names);
    }

    [Fact]
    public void BuildTools_adds_no_run_code_when_the_host_has_no_tools_even_with_git()
    {
        // A client that was never connected has no registered host tools. run_code may only appear when
        // the host has tools, so registering the git tools must not create a script entry point, and the
        // git surface must not be reachable from a script registry.
        var client = new ExtensionHostClient(new HostConnection { PipeName = "forge-git-tools-unstarted" });
        var runtime = NewRuntime(
            new GitTools(new GitCli(), new GitToolSettings(), new GitReviewLedger()),
            client);

        var tools = runtime.BuildTools();

        var runCode = tools.SingleOrDefault(
            static tool => string.Equals(tool.Definition.Name, "run_code", StringComparison.Ordinal));
        Assert.Null(runCode);
        Assert.Equal(ExpectedGitTools, tools.Select(static tool => tool.Definition.Name));
    }

    [Theory]
    [InlineData(GitTools.StatusName)]
    [InlineData(GitTools.LogName)]
    [InlineData(GitTools.DiffName)]
    [InlineData(GitTools.FetchName)]
    public void Readonly_git_tools_evaluate_to_Allow_under_the_configured_policy(string toolName)
    {
        var policy = ConfiguredPolicy();

        var mode = policy.Evaluate(
            new ApprovalRequest(toolName, "{}", ToolSideEffect.ReadOnly, TestWorkDir()));

        Assert.Equal(ApprovalMode.Allow, mode);
    }

    [Theory]
    [InlineData(GitTools.BranchName, ToolSideEffect.Write)]
    [InlineData(GitTools.SyncName, ToolSideEffect.Write)]
    public void Write_git_tools_do_not_evaluate_to_Allow_under_the_configured_policy(string toolName, ToolSideEffect sideEffect)
    {
        var policy = ConfiguredPolicy();

        var mode = policy.Evaluate(new ApprovalRequest(toolName, "{}", sideEffect, TestWorkDir()));

        // An ask without a handler is a denial; the point is that a write never becomes an allow.
        Assert.Equal(ApprovalMode.Ask, mode);
    }

    /// <summary>
    /// The policy <see cref="SessionRuntime.Build"/> configures into its host. It is a private static
    /// (<c>SessionPolicy</c>), so this fetches the configured instance instead of rebuilding it - the pinned
    /// behavior is the configuration the app actually runs with, not a copy of it.
    /// </summary>
    private static IApprovalPolicy ConfiguredPolicy()
    {
        var field = typeof(SessionRuntime).GetField("SessionPolicy", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "SessionRuntime.SessionPolicy is missing; the approval contract moved. Update this test.");
        return (IApprovalPolicy)(field.GetValue(null) ?? throw new InvalidOperationException("SessionPolicy was null."));
    }

    private static SessionRuntime NewRuntime(GitTools? git, ExtensionHostClient? client = null)
    {
        var services = new AgentRuntimeServices(
            static _ => throw new InvalidOperationException("no provider in git surface tests"),
            client,
            null!,
            null!,
            null!,
            new ModelSettings("test"),
            Git: git);
        var session = new SessionInfo("session", TestWorkDir(), "title", DateTimeOffset.UtcNow);
        return new SessionRuntime(services, session);
    }

    private static string TestWorkDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "forge-git-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
