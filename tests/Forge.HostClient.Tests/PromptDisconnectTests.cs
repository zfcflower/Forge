using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

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
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var contributor = await RegisterPromptAsync(host, client, "alpha", "tone");
        var directory = BoundRoots.AbsoluteDir("prompt-drop");
        var arrived = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On("plugin.prompt", async (message, ct) =>
        {
            arrived.TrySetResult(message);
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return Json.Element("""{"text":"late"}""");
        });

        var evaluation = client.GetPluginPromptAsync(
            contributor, new ToolContext("sess", directory), CancellationToken.None);
        var request = await arrived.Task.Bounded();
        Assert.Equal("alpha", request.Arguments!.Value.GetProperty("pluginId").GetString());
        Assert.Equal("tone", request.Arguments!.Value.GetProperty("contributorId").GetString());

        await host.DropConnectionAsync();
        gate.TrySetResult();

        var text = await evaluation.Bounded();
        Assert.Null(text);
        Assert.NotEmpty(client.Diagnostics);
    }

    [Fact]
    public async Task Caller_cancellation_still_throws()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var limit = TestLimit.Create();
        var contributor = await RegisterPromptAsync(host, client, "alpha", "tone");
        var directory = BoundRoots.AbsoluteDir("prompt-cancel");
        var arrived = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On("plugin.prompt", async (message, ct) =>
        {
            arrived.TrySetResult(message);
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return Json.Element("""{"text":"late"}""");
        });

        using var caller = new CancellationTokenSource();
        var evaluation = client.GetPluginPromptAsync(
            contributor, new ToolContext("sess", directory), caller.Token);
        await arrived.Task.Bounded();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation.Bounded());
        gate.TrySetResult();
    }

    private static async Task<PluginPromptReference> RegisterPromptAsync(
        FakeHost host, ExtensionHostClient client, string pluginId, string contributorId)
    {
        await host.NotifyClientAsync("contributions.register",
            "{\"pluginId\":\"" + pluginId + "\",\"contributions\":{\"promptContributors\":[{\"id\":\""
            + contributorId + "\"}]}}");
        await host.SyncAsync();
        return client.PromptContributors.Single(p =>
            p.PluginId == pluginId && p.ContributorId == contributorId);
    }
}
