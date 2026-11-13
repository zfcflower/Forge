using System.Net;
using System.Text;
using Forge.Abstractions;
using Forge.App.Automations;
using Forge.App.Tests.Automations;
using Forge.App.Providers;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// Pins the three entry points of a catalog refresh and what each is allowed to do: the daily run always asks the source
/// (conditionally) and always refreshes the provider lists, the manual and startup paths do nothing when today's snapshot is
/// already on disk, and the startup path never touches a provider's own model list.
/// </summary>
public sealed class ModelCatalogRefreshTests : IDisposable
{
    private const string CatalogJson = """
    {"anthropic":{"id":"anthropic","models":{"claude-sonnet-4-5":{"limit":{"context":200000,"output":64000},"cost":{"input":3,"output":15}}}}}
    """;

    private const string ModelListJson = """{"data":[{"id":"ai-model","context_length":32000}]}""";

    private const string Settings = """
    {
      "providers": {
        "version": 1,
        "defaultId": "ai",
        "items": [
          {
            "id": "ai",
            "preset": "custom-openai",
            "name": "AI",
            "protocol": "openai-compatible",
            "baseUrl": "https://ai.invalid/v1",
            "auth": { "kind": "api-key", "envVar": "FORGE_TEST_CATALOG_KEY" },
            "models": ["ai-model"],
            "defaultModel": "ai-model"
          }
        ]
      }
    }
    """;

    private readonly string root = TestTempFiles.NewDirectory("forge-catalog-refresh-");
    private readonly HttpClient http;
    private readonly ProviderServices providers;
    private readonly ModelCatalogRefreshService service;
    private readonly ScriptedHandler handler = new();

    public ModelCatalogRefreshTests()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, "settings.json"), Settings);
        // The provider is "ready" through its environment variable, so the list refresh reaches the fake endpoint instead of
        // being skipped as a missing key (which is its own, correct, skip).
        Environment.SetEnvironmentVariable("FORGE_TEST_CATALOG_KEY", "test-key");
        http = new HttpClient(handler);
        providers = new ProviderServices(Path.Combine(root, "settings.json"), Path.Combine(root, "data"), http);
        service = new ModelCatalogRefreshService(providers, http);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FORGE_TEST_CATALOG_KEY", null);
        http.Dispose();
        TestTempFiles.DeleteQuietly(root);
    }

    [Fact]
    public async Task The_startup_check_fetches_the_snapshot_and_leaves_the_provider_lists_alone()
    {
        handler.Catalog(HttpStatusCode.OK, CatalogJson, "\"v1\"");

        var result = await service.RefreshAsync(ModelCatalogRefreshMode.Startup, CancellationToken.None);

        Assert.Equal(ModelCatalogRefreshOutcome.Updated, result.Outcome);
        Assert.Equal(1, result.ProviderCount);
        Assert.Equal(200_000, providers.ModelCatalog.View.Lookup(ProviderPresets.AnthropicId, "claude-sonnet-4-5").ContextWindow);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("https://models.dev/api.json", handler.Requests[0].AbsoluteUri);
    }

    [Fact]
    public async Task The_startup_check_does_nothing_when_todays_snapshot_is_already_here()
    {
        handler.Catalog(HttpStatusCode.OK, CatalogJson, "\"v1\"");
        await service.RefreshAsync(ModelCatalogRefreshMode.Startup, CancellationToken.None);
        var callsAfterFirst = handler.Calls;

        var second = await service.RefreshAsync(ModelCatalogRefreshMode.Startup, CancellationToken.None);

        Assert.Equal(ModelCatalogRefreshOutcome.Skipped, second.Outcome);
        Assert.Equal(callsAfterFirst, handler.Calls);
    }

    [Fact]
    public async Task The_daily_run_asks_again_and_a_304_keeps_the_snapshot()
    {
        handler.Catalog(HttpStatusCode.OK, CatalogJson, "\"v1\"");
        await service.RefreshAsync(ModelCatalogRefreshMode.Startup, CancellationToken.None);
        handler.Catalog(HttpStatusCode.NotModified, string.Empty, null);

        var result = await service.RefreshAsync(ModelCatalogRefreshMode.Scheduled, CancellationToken.None);

        Assert.Equal(ModelCatalogRefreshOutcome.NotModified, result.Outcome);
        // The snapshot on disk is untouched, and the conditional request carried its validator.
        Assert.Equal("claude-sonnet-4-5", Assert.Single(providers.ModelCatalog.View.Snapshot!.Providers["anthropic"].Keys));
        Assert.Equal("\"v1\"", handler.Requests.Last(r => r.Uri.AbsolutePath.EndsWith("api.json", StringComparison.Ordinal)).IfNoneMatch);
    }

    [Fact]
    public async Task The_daily_run_refreshes_every_ready_provider_list()
    {
        handler.Catalog(HttpStatusCode.OK, CatalogJson, "\"v1\"");
        handler.Models(ModelListJson);

        var result = await service.RefreshAsync(ModelCatalogRefreshMode.Scheduled, CancellationToken.None);

        Assert.Equal(ModelCatalogRefreshOutcome.Updated, result.Outcome);
        Assert.Equal(1, result.ProviderLists);
        Assert.Equal(32_000, Assert.Single(providers.Catalog.Providers).MetadataFor("ai-model").ContextWindow);
    }

    [Fact]
    public async Task A_failing_provider_list_does_not_fail_the_refresh()
    {
        handler.Catalog(HttpStatusCode.OK, CatalogJson, "\"v1\"");
        handler.ModelsStatus(HttpStatusCode.InternalServerError);

        var result = await service.RefreshAsync(ModelCatalogRefreshMode.Scheduled, CancellationToken.None);

        Assert.Equal(ModelCatalogRefreshOutcome.Updated, result.Outcome);
        Assert.Equal(0, result.ProviderLists);
        Assert.NotNull(result.ProviderProblems);
        Assert.Single(result.ProviderProblems!);
    }

    [Fact]
    public async Task A_failed_fetch_keeps_the_catalog_the_app_already_had()
    {
        handler.Catalog(HttpStatusCode.OK, CatalogJson, "\"v1\"");
        await service.RefreshAsync(ModelCatalogRefreshMode.Startup, CancellationToken.None);
        handler.Catalog(HttpStatusCode.ServiceUnavailable, string.Empty, null);

        var result = await service.RefreshAsync(ModelCatalogRefreshMode.Scheduled, CancellationToken.None);

        Assert.Equal(ModelCatalogRefreshOutcome.Failed, result.Outcome);
        Assert.Contains("503", result.Failure);
        // The old snapshot still answers.
        Assert.Equal(200_000, providers.ModelCatalog.View.Lookup(ProviderPresets.AnthropicId, "claude-sonnet-4-5").ContextWindow);
    }

    [Fact]
    public async Task A_manual_refresh_is_skipped_when_todays_snapshot_is_already_here()
    {
        handler.Catalog(HttpStatusCode.OK, CatalogJson, "\"v1\"");
        await service.RefreshAsync(ModelCatalogRefreshMode.Startup, CancellationToken.None);
        var calls = handler.Calls;

        var result = await service.RefreshAsync(ModelCatalogRefreshMode.Manual, CancellationToken.None);

        Assert.Equal(ModelCatalogRefreshOutcome.Skipped, result.Outcome);
        Assert.Equal(calls, handler.Calls);
        Assert.Equal(ModelCatalogRefreshOutcome.Skipped, service.LastResult?.Outcome);
    }

    [Fact]
    public async Task The_run_outcome_follows_the_trigger_source()
    {
        var runner = new ModelCatalogRefreshRunner(service);

        handler.Catalog(HttpStatusCode.OK, CatalogJson, "\"v1\"");
        var startup = await runner.RunAsync(Context(AutomationTriggerSource.Event), CancellationToken.None);
        Assert.Equal(AutomationRunStatus.Completed, startup.Status);

        // A scheduled run has already-here data but must still ask; the fake answers 200 again.
        handler.Catalog(HttpStatusCode.OK, CatalogJson, "\"v2\"");
        var scheduled = await runner.RunAsync(Context(AutomationTriggerSource.Schedule), CancellationToken.None);
        Assert.Equal(AutomationRunStatus.Completed, scheduled.Status);
        Assert.False(string.IsNullOrWhiteSpace(scheduled.Message));

        handler.Catalog(HttpStatusCode.BadGateway, string.Empty, null);
        var failed = await runner.RunAsync(Context(AutomationTriggerSource.Schedule), CancellationToken.None);
        Assert.Equal(AutomationRunStatus.Failed, failed.Status);

        var skipped = await runner.RunAsync(Context(AutomationTriggerSource.Manual), CancellationToken.None);
        Assert.Equal(AutomationRunStatus.Skipped, skipped.Status);
    }

    [Fact]
    public async Task A_second_caller_while_one_refresh_runs_is_skipped()
    {
        // The first call parks inside the HTTP request; the second must not start a parallel fetch.
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.HoldCatalog = hold;
        handler.Catalog(HttpStatusCode.OK, CatalogJson, "\"v1\"");

        var first = service.RefreshAsync(ModelCatalogRefreshMode.Startup, CancellationToken.None);
        var second = await service.RefreshAsync(ModelCatalogRefreshMode.Scheduled, CancellationToken.None);

        Assert.Equal(ModelCatalogRefreshOutcome.Skipped, second.Outcome);
        hold.SetResult();
        Assert.Equal(ModelCatalogRefreshOutcome.Updated, (await first).Outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_changed_address_does_not_send_the_old_sources_validator()
    {
        // The disk holds a snapshot from the default source; after the address changes, the old ETag must stay home.
        var now = DateTimeOffset.UtcNow;
        providers.ModelCatalog.Apply(new ModelCatalogSnapshot(
            "https://models.dev/api.json",
            "\"old\"",
            now,
            new Dictionary<string, IReadOnlyDictionary<string, ModelCatalogModel>>(StringComparer.Ordinal)));
        await providers.SaveAsync(
            [.. providers.Catalog.Providers.Select(provider => provider.Definition)],
            providers.Catalog.Default?.Id,
            default,
            "https://mirror.invalid/api.json");
        handler.Catalog(HttpStatusCode.OK, CatalogJson, "\"new\"");

        var result = await service.RefreshAsync(ModelCatalogRefreshMode.Scheduled, CancellationToken.None);

        Assert.Equal(ModelCatalogRefreshOutcome.Updated, result.Outcome);
        var catalogRequest = handler.Requests.First(r => r.Uri.AbsolutePath.EndsWith("api.json", StringComparison.Ordinal));
        Assert.Equal("https://mirror.invalid/api.json", catalogRequest.AbsoluteUri);
        Assert.Null(catalogRequest.IfNoneMatch);
    }

    [Fact]
    public async Task A_304_still_counts_as_todays_check()
    {
        handler.Catalog(HttpStatusCode.OK, CatalogJson, "\"v1\"");
        await service.RefreshAsync(ModelCatalogRefreshMode.Startup, CancellationToken.None);
        handler.Catalog(HttpStatusCode.NotModified, string.Empty, null);
        Assert.Equal(ModelCatalogRefreshOutcome.NotModified, (await service.RefreshAsync(ModelCatalogRefreshMode.Scheduled, CancellationToken.None)).Outcome);

        // Nothing new arrived, but the source was asked: a later startup check must not fetch again.
        var calls = handler.Calls;
        Assert.Equal(ModelCatalogRefreshOutcome.Skipped, (await service.RefreshAsync(ModelCatalogRefreshMode.Startup, CancellationToken.None)).Outcome);
        Assert.Equal(calls, handler.Calls);
        Assert.True(providers.ModelCatalog.IsFromToday(TimeZoneInfo.Local));
    }

    [Fact]
    public async Task A_cancelled_caller_is_told_so_instead_of_starting_a_refresh()
    {
        handler.Catalog(HttpStatusCode.OK, CatalogJson, "\"v1\"");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RefreshAsync(ModelCatalogRefreshMode.Scheduled, cancelled.Token));
        Assert.Equal(0, handler.Calls);
    }

    private static RunnerContext Context(AutomationTriggerSource source) => new(
        new AutomationRun("run-1", BuiltInAutomations.ModelCatalogRefresh, "Model catalog update", source, "occ", AutomationRunStatus.Running, DateTimeOffset.UtcNow),
        new AutomationDefinition(BuiltInAutomations.ModelCatalogRefresh, BuiltInAutomations.ModelCatalogRefresh));

    /// <summary>Answers the catalog and the provider list by URI, and records what was asked (including validators).</summary>
    /// <summary>
    /// A signed-in Claude provider lists the account's models instead of keeping its three seeds: the request goes to the
    /// versioned endpoint the messages path uses (with the page size that returns the whole catalogue), carries the
    /// account's own token, and what comes back is what the model menu then offers.
    /// </summary>
    [Fact]
    public async Task A_claude_login_lists_the_accounts_models_through_the_versioned_endpoint()
    {
        var directory = TestTempFiles.NewDirectory("forge-claude-listing-");
        try
        {
            var data = Path.Combine(directory, "data");
            Directory.CreateDirectory(data);
            var credentials = Path.Combine(directory, "claude-credentials.json");
            var expiresAt = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeMilliseconds();
            File.WriteAllText(
                credentials,
                $$$"""
                {"claudeAiOauth":{"accessToken":"test-claude-token","expiresAt":{{{expiresAt}}}}}
                """);
            var settings = Path.Combine(directory, "settings.json");
            File.WriteAllText(
                settings,
                """
                {
                  "providers": {
                    "version": 1,
                    "defaultId": "claude",
                    "items": [
                      {
                        "id": "claude",
                        "preset": "anthropic",
                        "name": "Claude",
                        "protocol": "anthropic",
                        "baseUrl": "https://api.anthropic.com",
                        "auth": { "kind": "claude-subscription" },
                        "models": ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"],
                        "defaultModel": "claude-opus-5-5"
                      }
                    ]
                  }
                }
                """);
            handler.Models(
                """{"data":[{"id":"claude-fable-5-2"},{"id":"claude-opus-5-5"},{"id":"claude-haiku-5-5"}],"has_more":false}""");
            var services = new ProviderServices(
                settings, data, http, claudeCredentialsPath: credentials);
            var provider = Assert.Single(services.Catalog.Providers);

            var list = await services.RefreshModelsAsync(provider);

            Assert.Equal(["claude-fable-5-2", "claude-opus-5-5", "claude-haiku-5-5"], list.Ids);
            var request = handler.Requests[^1];
            Assert.Equal("https://api.anthropic.com/v1/models?limit=1000", request.AbsoluteUri);
            Assert.Equal("Bearer test-claude-token", request.Authorization);

            // The menu offers what the account serves; the stored default stays the one the provider was saved with.
            Assert.Equal(["claude-fable-5-2", "claude-opus-5-5", "claude-haiku-5-5"], provider.Models);
            Assert.Equal("claude-opus-5-5", provider.DefaultModel);
        }
        finally
        {
            TestTempFiles.DeleteQuietly(directory);
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private Func<HttpResponseMessage> catalog = () => new(HttpStatusCode.NotFound);
        private Func<HttpResponseMessage>? models;

        /// <summary>Set to park the next catalog request until it completes (the single-flight test).</summary>
        public TaskCompletionSource? HoldCatalog { get; set; }

        public List<Recorded> Requests { get; } = [];

        public int Calls => Requests.Count;

        public void Catalog(HttpStatusCode status, string body, string? etag) => catalog = () =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (etag is not null)
            {
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            }

            return response;
        };

        public void Models(string body) => models = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        public void ModelsStatus(HttpStatusCode status) => models = () => new HttpResponseMessage(status)
        {
            Content = new StringContent(string.Empty),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(new Recorded(
                uri,
                request.Headers.TryGetValues("If-None-Match", out var values) ? string.Join(",", values) : null,
                request.Headers.Authorization?.ToString()));
            if (uri.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                return models is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : models();
            }

            if (HoldCatalog is { } hold)
            {
                HoldCatalog = null;
                await hold.Task.ConfigureAwait(false);
            }

            return catalog();
        }

        public sealed record Recorded(Uri Uri, string? IfNoneMatch, string? Authorization)
        {
            public string AbsoluteUri => Uri.AbsoluteUri;
        }
    }
}
