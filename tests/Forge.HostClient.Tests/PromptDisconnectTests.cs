using Forge.Abstractions;
using Forge.Ipc;
using Xunit;
using static Forge.HostClient.Tests.ToolFixtures;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.HostClient.Tests;

/// <summary>
/// Prompt evaluation across a mid-flight transport drop. Once the registered prompt request has reached
/// the fake host, closing the host connection under the TCS barrier must surface as a null prompt plus a
/// diagnostic, never as a throw. A caller that cancels its own request still observes
/// <see cref="OperationCanceledException"/>. Status queries may legitimately throw on a disconnect, so
/// they are not covered here.
/// </summary>
public sealed class PromptDisconnectTests
{
    [Fact]
    public async Task Mid_flight_transport_drop_returns_null_with_a_diagnostic()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var contributor = await RegisterPromptAsync(harness.Host, harness.Client, "alpha", "tone");
        var directory = BoundRoots.AbsoluteDir("prompt-drop");
        var arrived = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Host.On(Sdk.ExtensionProtocol.PluginPrompt, async (message, ct) =>
        {
            arrived.TrySetResult(message);
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return new Sdk.PromptOutput("late");
        });

        var evaluation = harness.Client.GetPluginPromptAsync(
            contributor, new ToolContext("sess", directory), CancellationToken.None);
        var request = await arrived.Task.Bounded();
        var sent = request.Read<Sdk.PromptExecutionRequest>();
        Assert.Equal("alpha", sent.PluginId);
        Assert.Equal("tone", sent.ContributorId);

        await harness.Host.DropConnectionAsync();
        gate.TrySetResult();

        var text = await evaluation.Bounded();
        Assert.Null(text);
        Assert.NotEmpty(harness.Client.Diagnostics);
    }

    [Fact]
    public async Task Caller_cancellation_still_throws()
    {
        await using var harness = await HostClientHarness.StartAsync();
        var contributor = await RegisterPromptAsync(harness.Host, harness.Client, "alpha", "tone");
        var directory = BoundRoots.AbsoluteDir("prompt-cancel");
        var arrived = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Host.On(Sdk.ExtensionProtocol.PluginPrompt, async (message, ct) =>
        {
            arrived.TrySetResult(message);
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return new Sdk.PromptOutput("late");
        });

        using var caller = new CancellationTokenSource();
        var evaluation = harness.Client.GetPluginPromptAsync(
            contributor, new ToolContext("sess", directory), caller.Token);
        await arrived.Task.Bounded();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation.Bounded());
        gate.TrySetResult();
    }

}
