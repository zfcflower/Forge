using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the shape that raises the "the prompt cache was rewritten from scratch" toast, and the sentence it shows. The toast itself is
/// one call into the notification service; what is worth pinning is which turns count and what the user is told.
/// </summary>
[Collection("forge-app-ui")]
public sealed class CacheRewriteNoticeTests
{
    [Theory]
    [InlineData("root", 1_000, 0, true)]      // wrote the prefix, read nothing back: the expensive shape
    [InlineData("root", 1_000, 500, false)]   // read part of it: the cache did its job
    [InlineData("root", 0, 0, false)]         // wrote nothing: nothing to rewrite
    [InlineData("scout-1", 1_000, 0, false)]  // a sub-agent's own spend stays on its tab
    public void Only_a_root_turn_that_wrote_and_read_nothing_counts(string agentId, int write, int read, bool expected) =>
        Assert.Equal(expected, SessionWorkspaceViewModel.CountsAsFullRewrite(agentId, new TokenUsage(10, 1, read, write)));

    [Fact]
    public void The_sentence_names_the_tokens_and_the_likely_cause_but_never_advises_a_lifetime()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);

        var body = SessionStrings.CacheRewriteBody(1_234_567).Resolve();

        // The count is the evidence, the causes are what the user can act on. "Change the cache lifetime" is deliberately absent:
        // the auto policy already owns that decision, so repeated rewrites usually mean the prefix changed instead.
        Assert.Contains("1,234,567", body, StringComparison.Ordinal);
        Assert.Contains("工具集", body, StringComparison.Ordinal);
        // It may name the lifetime only to rule it out ("rather than the lifetime being too short"); what it must not do is tell the
        // user to change a setting, because the auto policy already owns that decision.
        Assert.Contains("而不是", body, StringComparison.Ordinal);
        Assert.DoesNotContain("promptCacheTtl", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_sentence_in_english()
    {
        using var language = new LanguageScope(UiLanguage.English);

        var body = SessionStrings.CacheRewriteBody(1_234_567).Resolve();

        Assert.Contains("1,234,567", body, StringComparison.Ordinal);
        Assert.Contains("tool set", body, StringComparison.Ordinal);
        Assert.Contains("rather than", body, StringComparison.Ordinal);
        Assert.DoesNotContain("promptCacheTtl", body, StringComparison.Ordinal);
    }
}
