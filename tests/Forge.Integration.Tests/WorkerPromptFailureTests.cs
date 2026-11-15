using Forge.Abstractions;
using Forge.HostClient;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>
/// F-PW-13: a prompt contributor that throws is a failed evaluation, not a dead worker. The worker answers with a
/// classified <c>prompt_failed</c> instead of letting the exception escape as an unclassified <c>handler_error</c>, and the
/// host charges it against the plugin's failure budget - so a contributor that keeps failing eventually has its worker
/// refused (<c>worker_disabled</c>), exactly as the host contract "commands and prompts go through the same abnormal event
/// and failure budget" requires. Before the fix the host neither caught the worker's error frame nor charged the budget, so
/// a repeatedly failing contributor never stopped the worker and the main program saw an unclassified error.
/// </summary>
public sealed class WorkerPromptFailureTests
{
    /// <summary>Mirrors <c>PluginState.FailureBudget</c>: consecutive abnormal events before the worker is refused.</summary>
    private const int FailureBudget = 4;

    [Fact]
    public async Task Repeatedly_failing_prompt_contributor_exhausts_the_failure_budget_and_disables_the_worker()
    {
        await using var fx = E2eFixture.AllowingApprovals();
        fx.WritePlugin("boom", PluginSources.ThrowingPrompt("boom"));
        await fx.StartAndApproveViaStatusAsync("boom");
        await fx.WaitForToolsAsync("boom", 1, "the boom ping tool");
        await fx.WaitUntilAsync(() => fx.Client.PromptContributors.Count == 1, "the boom prompt contributor");

        // Baseline: the healthy tool runs, so the worker is up and the failure budget starts clear (a completed call
        // resets it). Everything below is uninterrupted failures, so the count only climbs.
        await fx.AssertToolContentAsync("boom", "ping", "{}", "pong");

        // Each throwing evaluation is a soft null to the main program, but charges one failure on the host.
        var contributor = new PluginPromptReference("boom", "boom");
        for (var attempt = 0; attempt < FailureBudget; attempt++)
        {
            var text = await fx.Client.GetPluginPromptAsync(
                contributor, new ToolContext("prompt-fail-" + attempt, fx.Root), fx.Ct);
            Assert.Null(text);
        }

        // The budget is now exhausted: the same plugin's healthy tool is refused with worker_disabled until a refresh,
        // which is the observable proof that the prompt failures were charged against the plugin's failure budget.
        var after = await fx.InvokeAsync("boom", "ping", "{}");
        Assert.True(after.IsError, $"the tool call after the budget was exhausted should fail: {after.Content}");
        Assert.Contains("worker_disabled", after.Content, StringComparison.Ordinal);
    }
}
