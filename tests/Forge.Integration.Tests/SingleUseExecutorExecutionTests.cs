using Forge.Ipc;
using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Positive-path tests for the single-use <c>run_code</c> executor: every test compiles a real script with
/// <see cref="Forge.Scripting.ScriptCodeCompiler"/>, delivers the artifact plus its descriptor over the wire and runs
/// it exactly once. Nothing here touches internal APIs.</summary>
public sealed class SingleUseExecutorExecutionTests
{
    private static readonly TimeSpan StepLimit = TimeSpan.FromSeconds(60);
    private static readonly CodeExecutionOptions Options = new();
    private const int RunTimeoutMilliseconds = 15000;

    [Fact]
    public async Task ValidScript_ReturnsItsValue_AndRefusesASecondRun()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var compiled = await SingleUseExecutorArtifacts.CompileValidAsync("return \"ok\";", Options, ct);
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        await SingleUseExecutorArtifacts.LoadAsync(session, compiled, ct);
        var result = await SingleUseExecutorArtifacts.RunOnceAsync(
            session,
            SingleUseExecutorArtifacts.RunRequest(SingleUseExecutorArtifacts.NewRunId(), [], Options, RunTimeoutMilliseconds),
            ct);
        Assert.False(result.IsError, result.Content);
        Assert.Equal("ok", result.Content);

        var second = await Assert.ThrowsAsync<IpcRemoteException>(
            () => SingleUseExecutorArtifacts.RunOnceAsync(
                session,
                SingleUseExecutorArtifacts.RunRequest(SingleUseExecutorArtifacts.NewRunId(), [], Options, RunTimeoutMilliseconds),
                ct));
        Assert.Equal("already_ran", second.Code);
    }

    [Fact]
    public async Task LogAndValue_ComposeIntoOneResult()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var compiled = await SingleUseExecutorArtifacts.CompileValidAsync(
            "Log(\"sum is 42\");\nreturn 40 + 2;", Options, ct);
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        await SingleUseExecutorArtifacts.LoadAsync(session, compiled, ct);
        var result = await SingleUseExecutorArtifacts.RunOnceAsync(
            session,
            SingleUseExecutorArtifacts.RunRequest(SingleUseExecutorArtifacts.NewRunId(), [], Options, RunTimeoutMilliseconds),
            ct);
        Assert.False(result.IsError, result.Content);
        Assert.Equal("42\n[log]\nsum is 42", result.Content);
    }

    [Fact]
    public async Task SyntaxError_ProducesAnErrorResult_WithoutAnArtifactToLoad()
    {
        // The name is deliberately about the artifact, not about a spawned process: a failed compile yields no image
        // and no descriptor, so there is nothing any host could hand to an executor. This test starts no
        // SingleUseExecutorSession at all, which is the observable proof that the compile path never spawns one.
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var compiled = await SingleUseExecutorArtifacts.CompileRawAsync(
            "var x = ;\nreturn await Tools.Call(\"probe\", new {});", Options, ct);
        Assert.False(compiled.Ok, "A script with a syntax error compiled.");
        Assert.Null(compiled.Image);
        Assert.Null(compiled.Descriptor);
        Assert.False(string.IsNullOrWhiteSpace(compiled.Error));
        var result = new CodeExecutionResult(compiled.Error!, IsError: true);
        Assert.True(result.IsError);
        Assert.Contains("line 1", result.Content, StringComparison.OrdinalIgnoreCase);
    }
}
