using System.Text.Json;
using Forge.Abstractions;
using Xunit;

namespace Forge.Integration.Tests;

public sealed class ApprovalRaceTests
{
    [Fact]
    public async Task Execute_racing_revoke_fails_the_inflight_call_and_blocks_every_later_call()
    {
        await using var fx = new E2eFixture();
        var marker = Path.Combine(fx.Root, "race-started.txt");
        await fx.StartWithApprovedPluginAsync("alpha", "a", PluginSources.AllTools.Length);

        var workerPid = await fx.WorkerPidAsync("alpha");
        var staleEcho = fx.ToolOf("alpha", "echo");
        var sleepTool = fx.ToolOf("alpha", "sleep");
        var inflight = sleepTool.InvokeAsync(
            new ToolCall(Guid.NewGuid().ToString("N"), "sleep", JsonSerializer.Serialize(new { ms = 30_000, marker })),
            new ToolContext("race-session", fx.Root),
            fx.Ct).AsTask();
        await fx.WaitUntilAsync(() => File.Exists(marker), "the sleeping tool to start");

        await fx.Client.RevokePluginAsync("alpha", "1.0.0", fx.Ct);

        var outcome = await inflight;
        Assert.True(outcome.IsError, outcome.Content);
        Assert.Contains("alpha", outcome.Content, StringComparison.Ordinal);
        Assert.Contains("sleep", outcome.Content, StringComparison.Ordinal);

        await fx.WaitForToolsAsync("alpha", 0, "alpha tools to be unregistered");
        Assert.Empty(fx.Client.Tools);
        Assert.Equal("Draft", fx.ReadRecord("alpha")!.Status);
        await fx.WaitForExitAsync(workerPid, "the revoked worker");

        var late = await fx.InvokeAsync(staleEcho, """{"text":"late"}""");
        Assert.True(late.IsError);
        Assert.Contains("must not run", late.Content, StringComparison.Ordinal);
    }
}
