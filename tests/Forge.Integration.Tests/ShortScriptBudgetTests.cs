using System.Text.Json;
using Forge.Abstractions;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Warm short budgets stay usable and overruns still fail; isolated from parallel Roslyn load.</summary>
[Collection("ShortScriptBudget")]
public sealed class ShortScriptBudgetTests
{
    private const string BriefScript = """await Task.Delay(100); return "budget-ok";""";

    [Fact]
    public async Task Small_budget_runs_a_brief_script_once_warm()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var runCode = fx.Client.CreateRunCodeTool(new EmptyRegistry());

        var prewarmed = await InvokeScriptAsync(fx, runCode, BriefScript, TimeSpan.FromSeconds(60));
        Assert.False(prewarmed.IsError, prewarmed.Content);
        Assert.Equal("budget-ok", prewarmed.Content);

        var small = await InvokeScriptAsync(fx, runCode, BriefScript, TimeSpan.FromSeconds(1.5));
        Assert.False(small.IsError, small.Content);
        Assert.Equal("budget-ok", small.Content);

        var healthy = await InvokeScriptAsync(fx, runCode, BriefScript, TimeSpan.FromSeconds(60));
        Assert.False(healthy.IsError, healthy.Content);
        Assert.Equal("budget-ok", healthy.Content);
    }

    [Fact]
    public async Task A_script_that_overruns_its_small_budget_still_fails()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var runCode = fx.Client.CreateRunCodeTool(new EmptyRegistry());

        var prewarmed = await InvokeScriptAsync(fx, runCode, BriefScript, TimeSpan.FromSeconds(60));
        Assert.False(prewarmed.IsError, prewarmed.Content);

        var overrun = await InvokeScriptAsync(
            fx, runCode, """await Task.Delay(5000); return "never";""", TimeSpan.FromSeconds(1));
        Assert.True(overrun.IsError, overrun.Content);
        Assert.Contains("1 s", overrun.Content, StringComparison.Ordinal);
    }

    private static Task<ToolResult> InvokeScriptAsync(
        E2eFixture fx, ITool runCode, string code, TimeSpan timeout) =>
        runCode.InvokeAsync(
            new ToolCall(
                Guid.NewGuid().ToString("N"),
                "run_code",
                JsonSerializer.Serialize(new { code, timeout_seconds = timeout.TotalSeconds })),
            new ToolContext("short-budget", fx.Root),
            fx.Ct).AsTask();

    private sealed class EmptyRegistry : IToolRegistry
    {
        public IReadOnlyList<ITool> Tools => [];

        public bool TryGet(string name, out ITool tool)
        {
            tool = null!;
            return false;
        }
    }
}
