using Forge.HostClient;
using Forge.Ipc;
using Xunit;

namespace Forge.Integration.Tests;

public sealed class ConcurrencyAndIsolationTests
{
    [Fact]
    public async Task Sixteen_parallel_calls_across_two_plugins_correlate_to_their_own_results()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        fx.WritePlugin("beta", PluginSources.Standard("beta", echoPrefix: "b"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.ApproveAsync("beta");
        await fx.WaitUntilAsync(
            () => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length &&
                  fx.ToolsOf("beta").Count == PluginSources.AllTools.Length,
            "tools of both plugins");

        var calls = Enumerable.Range(0, 16).Select(index =>
        {
            var plugin = index % 2 == 0 ? "alpha" : "beta";
            var tool = fx.ToolOf(plugin, "echo");
            var text = $"call-{index:D2}";
            return (Expected: $"{(plugin == "alpha" ? "a" : "b")}:{text}",
                    Pending: fx.InvokeAsync(tool, $$"""{"text":"{{text}}"}"""));
        }).ToArray();

        var results = await Task.WhenAll(calls.Select(call => call.Pending)).WaitAsync(fx.Ct);

        for (var index = 0; index < calls.Length; index++)
        {
            Assert.False(results[index].IsError, results[index].Content);
            Assert.Equal(calls[index].Expected, results[index].Content);
        }
    }

    [Fact]
    public async Task Parallel_calls_to_one_plugin_each_return_their_own_result()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        var sleeps = Enumerable.Range(0, 3).Select(index =>
            fx.InvokeAsync("alpha", "sleep", $$"""{"ms":300,"text":"{{index}}"}"""))
            .ToArray();
        var echoes = Enumerable.Range(0, 3).Select(index =>
            fx.InvokeAsync("alpha", "echo", $$"""{"text":"e{{index}}"}"""))
            .ToArray();

        var echoResults = await Task.WhenAll(echoes).WaitAsync(fx.Ct);
        for (var index = 0; index < echoResults.Length; index++)
        {
            Assert.Equal($"a:e{index}", echoResults[index].Content);
        }

        var sleepResults = await Task.WhenAll(sleeps).WaitAsync(fx.Ct);
        Assert.All(sleepResults, result => Assert.Equal("slept:300", result.Content));
    }

    [Fact]
    public async Task Broken_plugin_is_refused_and_leaves_the_healthy_plugin_untouched()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        fx.WritePlugin("broken", PluginSources.Broken("broken"));
        await fx.StartAsync();

        var refused = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            fx.Client.ApprovePluginAsync("broken", "1.0.0", new string('a', 64), new string('b', 64), "x", fx.Ct));
        Assert.Equal("plugin_not_loadable", refused.Code);
        Assert.Empty(fx.ToolsOf("broken"));

        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");

        Assert.Empty(fx.ToolsOf("broken"));
        Assert.Equal(PluginSources.AllTools.Length, fx.Client.Tools.Count);
        Assert.All(fx.Client.Tools, tool => Assert.Equal("alpha", ((ExtensionHostTool)tool).PluginId));

        var echo = await fx.InvokeAsync("alpha", "echo", """{"text":"healthy"}""");
        Assert.False(echo.IsError, echo.Content);
        Assert.Equal("a:healthy", echo.Content);
    }

    [Fact]
    public async Task Broken_plugin_present_at_startup_does_not_block_approval_of_the_healthy_one()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("broken", PluginSources.Broken("broken"));
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));
        await fx.StartAsync();
        await fx.ApproveAsync("alpha");
        await fx.WaitUntilAsync(() => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools");
        Assert.Null(fx.ReadRecord("broken"));
    }
}
