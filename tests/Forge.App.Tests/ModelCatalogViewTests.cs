using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The catalogue lookup's drawer rule. A provider whose own name names a catalog provider gets that provider's entry
/// before any global fallback runs, so an aggregator re-listing the same model id (a figure it derives from the window,
/// not one the gateway reported) cannot claim the id — the leak that put a 943,718 reply cap on the wire and had every
/// request for a 131,072-cap model refused. The preset mapping stays ahead of the name, and the no-identity fallback
/// keeps gateways ahead of aggregators for the providers nobody can name.
/// </summary>
public sealed class ModelCatalogViewTests
{
    private static ModelCatalogView View(params (string Provider, string Id, int? MaxOutput)[] entries) =>
        ModelCatalogView.Create(ProviderCatalogFixture.Snapshot(entries));

    [Fact]
    public void A_provider_name_opens_its_own_drawer_even_when_the_global_fallback_would_pick_another()
    {
        // deepseek outranks opencode in the fallback order, so the global entry for glm-5.3 is its 999999; the drawer the
        // provider's name opens still answers with opencode's own 131072.
        var view = View(
            ("deepseek", "glm-5.3", 999_999),
            ("opencode", "glm-5.3", 131_072));

        var found = view.Find("custom-openai", "opencode", "OPENCODE", "glm-5.3");

        Assert.Equal(131_072, found!.MaxOutputTokens);
    }

    [Fact]
    public void The_display_name_matches_after_trim_and_case()
    {
        var view = View(("opencode", "glm-5.3", 131_072));

        var byName = view.Find("custom-openai", null, " OpenCode ", "glm-5.3");

        Assert.Equal(131_072, byName!.MaxOutputTokens);
    }

    [Fact]
    public void A_name_no_catalog_provider_carries_falls_back_to_the_global_index()
    {
        var view = View(("deepseek", "glm-5.3", 999_999));

        var global = view.Find("custom-openai", "my-proxy", "My Proxy", "glm-5.3");

        Assert.Equal(999_999, global!.MaxOutputTokens);
    }

    [Fact]
    public void A_known_preset_wins_over_a_name_that_names_a_different_provider()
    {
        var view = View(
            ("openrouter", "z-ai/glm-5.3", 943_718),
            ("opencode", "glm-5.3", 131_072));

        var preset = view.Find("openrouter", "opencode", "opencode", "glm-5.3");

        Assert.Equal(943_718, preset!.MaxOutputTokens);
    }

    [Fact]
    public void The_global_fallback_prefers_a_gateways_own_entry_over_an_aggregators_relisting()
    {
        // No identity to open a drawer with: the contested id goes to opencode, the gateway, not openrouter, the
        // aggregator that re-lists other providers' models with its own derived figures.
        var view = View(
            ("openrouter", "z-ai/glm-5.3", 943_718),
            ("opencode", "glm-5.3", 131_072));

        var global = view.Find("custom-openai", "my-proxy", null, "glm-5.3");

        Assert.Equal(131_072, global!.MaxOutputTokens);
    }
}
