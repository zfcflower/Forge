using System.Net;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// The composer's model menu across connected providers: the list is the union, each row says which provider serves it when
/// there is more than one, and the chosen row decides both the model and the provider of the next turn.
/// </summary>
public sealed class ComposerProviderTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-composer-providers-");


    [Fact]
    public void Two_providers_produce_a_union_with_a_provider_caption_per_row()
    {
        var composer = CreateComposer(
            """
            "items": [
              { "id": "alpha", "preset": "custom-openai", "name": "Alpha", "protocol": "openai-compatible",
                "baseUrl": "https://alpha.invalid/v1", "auth": { "kind": "api-key" },
                "models": ["alpha-1", "shared"], "defaultModel": "alpha-1" },
              { "id": "beta", "preset": "custom-openai", "name": "Beta", "protocol": "openai-compatible",
                "baseUrl": "https://beta.invalid/v1", "auth": { "kind": "api-key" },
                "models": ["beta-1", "shared"], "defaultModel": "beta-1" }
            ]
            """,
            defaultId: "alpha");

        Assert.Equal(
            ["alpha-1", "shared", "beta-1", "shared"],
            composer.Models.Select(static model => model.Name));
        Assert.All(composer.Models, static model => Assert.True(model.ShowProviderCaption));
        Assert.Equal(
            ["Alpha", "Alpha", "Beta", "Beta"],
            composer.Models.Select(static model => model.Caption));
        Assert.Equal("alpha-1", composer.Model);
    }

    [Fact]
    public void Picking_a_row_sets_both_the_model_and_the_provider_of_the_next_turn()
    {
        var composer = CreateComposer(
            """
            "items": [
              { "id": "alpha", "preset": "custom-openai", "name": "Alpha", "protocol": "openai-compatible",
                "baseUrl": "https://alpha.invalid/v1", "auth": { "kind": "api-key" },
                "models": ["alpha-1"], "defaultModel": "alpha-1" },
              { "id": "beta", "preset": "custom-openai", "name": "Beta", "protocol": "openai-compatible",
                "baseUrl": "https://beta.invalid/v1", "auth": { "kind": "api-key" },
                "models": ["beta-1"], "defaultModel": "beta-1" }
            ]
            """,
            defaultId: "alpha");

        var beta = composer.Models.Single(static model => model.Name == "beta-1");
        beta.SelectCommand.Execute(null);
        // The untouched default hands over null on purpose (the session runs as configured); raising the effort is what
        // makes the composer hand a definition over, and that definition must carry the picked provider.
        composer.Effort = ThinkingEffort.High;

        var definition = composer.CurrentDefinition();
        Assert.NotNull(definition);
        Assert.Equal("beta-1", definition!.Model.Model);
        Assert.Equal("beta", definition.Model.ProviderId);
    }

    [Fact]
    public void The_same_model_on_two_providers_switches_the_provider_the_next_turn_uses()
    {
        var composer = CreateComposer(
            """
            "items": [
              { "id": "alpha", "preset": "custom-openai", "name": "Alpha", "protocol": "openai-compatible",
                "baseUrl": "https://alpha.invalid/v1", "auth": { "kind": "api-key" },
                "models": ["shared"], "defaultModel": "shared" },
              { "id": "beta", "preset": "custom-openai", "name": "Beta", "protocol": "openai-compatible",
                "baseUrl": "https://beta.invalid/v1", "auth": { "kind": "api-key" },
                "models": ["shared"], "defaultModel": "shared" }
            ]
            """,
            defaultId: "alpha");

        var rows = composer.Models.Where(static model => model.Name == "shared").ToList();
        Assert.Equal(2, rows.Count);

        // Same model id, other provider: the name does not change, the selection does.
        rows[1].SelectCommand.Execute(null);
        Assert.Equal("beta", composer.ProviderId);
        Assert.True(rows[1].IsSelected);
        Assert.False(rows[0].IsSelected);

        composer.Effort = ThinkingEffort.High;
        var definition = composer.CurrentDefinition();
        Assert.NotNull(definition);
        Assert.Equal("beta", definition!.Model.ProviderId);
    }

    [Fact]
    public void A_single_provider_does_not_repeat_its_name_on_every_row()
    {
        var composer = CreateComposer(
            """
            "items": [
              { "id": "solo", "preset": "custom-openai", "name": "Solo", "protocol": "openai-compatible",
                "baseUrl": "https://solo.invalid/v1", "auth": { "kind": "api-key" },
                "models": ["solo-1"], "defaultModel": "solo-1" }
            ]
            """,
            defaultId: "solo");

        var row = Assert.Single(composer.Models);
        Assert.False(row.ShowProviderCaption);
        Assert.Equal(string.Empty, row.Caption);
    }

    [Fact]
    public void An_empty_providers_section_offers_no_model_rather_than_a_placeholder()
    {
        // Nothing connected means nothing to pick: the picker is empty and the settings banner says why.
        var services = Services(
            """{ "providers": { "version": 1, "defaultId": null, "items": [] } }""");
        var composer = new ComposerViewModel(
            services,
            _ => { },
            new ComposerPreferences(),
            string.Empty,
            skillSource: static () => []);

        Assert.False(services.Catalog.IsConfigured);
        Assert.Empty(composer.Models);
    }

    [Fact]
    public void Changing_to_a_model_with_fewer_steps_clamps_the_effort_to_an_accepted_one()
    {
        var composer = CreateComposer(
            """
            "items": [
              { "id": "solo", "preset": "custom-openai", "name": "Solo", "protocol": "openai-compatible",
                "baseUrl": "https://solo.invalid/v1", "auth": { "kind": "api-key" },
                "models": ["gpt-5.1", "limited"], "defaultModel": "gpt-5.1" }
            ]
            """,
            defaultId: "solo",
            thinkingLevels: static (model, _) => string.Equals(model, "limited", StringComparison.Ordinal)
                ? [ThinkingEffort.Off, ThinkingEffort.Low]
                : ThinkingLevels.For(model));
        composer.Effort = ThinkingEffort.High;
        Assert.Equal(ThinkingEffort.High, composer.Effort);

        composer.Models.Single(static model => model.Name == "limited").SelectCommand.Execute(null);

        Assert.Equal(ThinkingEffort.Low, composer.Effort);
        Assert.Equal([ThinkingEffort.Off, ThinkingEffort.Low], composer.EffortSteps);
        Assert.Equal(1.0, composer.EffortMaximum);
        Assert.Equal(1.0, composer.EffortValue);
        Assert.Equal(SessionStrings.ComposerEffortName(ThinkingEffort.Low), composer.EffortName);
        Assert.Equal(ThinkingEffort.Low, composer.CurrentDefinition()?.Model.Thinking);

        // Even a value set from code never lands above what the model accepts.
        composer.Effort = ThinkingEffort.Max;
        Assert.Equal(ThinkingEffort.Low, composer.Effort);
    }

    public void Dispose()
    {
        TestTempFiles.DeleteQuietly(root);
    }

    [Fact]
    public async Task Refreshing_the_picker_asks_the_providers_for_their_current_lists()
    {
        // The refresh button re-fetches every provider that can list models; a provider that cannot is skipped rather than
        // failing the whole refresh.
        using var handler = new ListingHandler("""{"data":[{"id":"alpha-2"}]}""");
        var composer = new ComposerViewModel(
            Services(
                """
                { "providers": { "version": 1, "defaultId": "alpha", "items": [
                  { "id": "alpha", "preset": "custom-openai", "name": "Alpha", "protocol": "openai-compatible",
                    "baseUrl": "https://alpha.invalid/v1", "auth": { "kind": "api-key" },
                    "models": ["alpha-1"], "defaultModel": "alpha-1" }
                ] } }
                """,
                handler),
            _ => { },
            new ComposerPreferences(),
            string.Empty,
            skillSource: static () => []);

        await composer.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Contains("alpha-2", composer.Models.Select(static model => model.Name));
    }

    /// <summary>Answers every request with one model list and counts the calls.</summary>
    private sealed class ListingHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private ComposerViewModel CreateComposer(
        string items,
        string defaultId,
        Func<string?, string?, IReadOnlyList<ThinkingEffort>>? thinkingLevels = null) =>
        new(
            Services("""{ "providers": { "version": 1, "defaultId": "{{DEFAULT}}", {{ITEMS}} } }"""
                .Replace("{{DEFAULT}}", defaultId, StringComparison.Ordinal)
                .Replace("{{ITEMS}}", items, StringComparison.Ordinal)),
            _ => { },
            new ComposerPreferences(),
            string.Empty,
            skillSource: static () => [],
            thinkingLevels: thinkingLevels);

    private ProviderServices Services(string json, HttpMessageHandler? handler = null)
    {
        var settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(settingsPath, json);
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        var http = handler is null
            ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new ProviderServices(settingsPath, data, http);
    }
}
