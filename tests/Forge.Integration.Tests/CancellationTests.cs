using System.Diagnostics;
using System.Text.Json;
using Forge.Abstractions;
using Xunit;

namespace Forge.Integration.Tests;

public sealed class CancellationTests
{
    [Fact]
    public async Task Cancelled_non_cooperative_tool_kills_the_worker_and_the_next_call_recovers()
    {
        await using var fx = new E2eFixture();
        var marker = Path.Combine(fx.Root, "cancel-started.txt");
        await fx.StartWithApprovedPluginAsync("alpha", "a", PluginSources.AllTools.Length);

        var pidBefore = await fx.WorkerPidAsync("alpha");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fx.Ct);
        var pending = fx.ToolOf("alpha", "sleep").InvokeAsync(
            new ToolCall(Guid.NewGuid().ToString("N"), "sleep", JsonSerializer.Serialize(new { ms = 60_000, marker })),
            new ToolContext("cancel-session", fx.Root),
            cancellation.Token).AsTask();
        await fx.WaitUntilAsync(() => File.Exists(marker), "the sleeping tool to start");

        cancellation.Cancel();
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var callerUnblocked = clock.Elapsed;

        await fx.WaitForExitAsync(pidBefore, "the cancelled worker");
        var workerKilled = clock.Elapsed;

        Assert.True(callerUnblocked < TimeSpan.FromSeconds(15), $"the cancelled caller waited {callerUnblocked}");
        Assert.True(workerKilled < TimeSpan.FromSeconds(20), $"the worker lived {workerKilled} after the cancel");

        await fx.AssertToolContentAsync("alpha", "echo", """{"text":"next"}""", "a:next");
        Assert.NotEqual(pidBefore, await fx.WorkerPidAsync("alpha"));
    }
}
