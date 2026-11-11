using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Providers;
using Forge.App.Settings;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// <see cref="ProviderCatalog"/> is where a stored definition becomes a usable provider: which provider a model request
/// reaches, what metadata a model carries, and — most of all — whether a row says "connected" only when it really is.
/// </summary>
public sealed class ProviderCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-provider-catalog-" + Guid.NewGuid().ToString("N"));
    private readonly ProviderSecretStore secrets;
    private readonly ProviderCredentials credentials;

    public ProviderCatalogTests()
    {
        Directory.CreateDirectory(root);
        secrets = new ProviderSecretStore(root);
        credentials = new ProviderCredentials(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            secrets,
            claudeCredentialsPath: Path.Combine(root, "no-claude-login.json"));
    }

    [Fact]
    public void The_default_is_returned_for_a_missing_id_and_an_unknown_id_falls_back_to_it()
    {
        var catalog = Catalog(
            [("first", ProviderCredentialKind.ApiKey), ("second", ProviderCredentialKind.ApiKey)],
            "second");

        Assert.Equal("second", catalog.Find(null)?.Id);
        Assert.Null(catalog.Find("nope"));
        Assert.Equal("second", catalog.CreateProvider(new ModelSettings("m"), new HttpClient()).Id);
    }

    [Fact]
    public void A_provider_with_a_stored_key_is_ready_and_one_without_a_key_is_not()
    {
        var catalog = Catalog(("keyed", ProviderCredentialKind.ApiKey), ("bare", ProviderCredentialKind.ApiKey));
        secrets.Set(catalog.Find("keyed")!.Definition.SecretReference!, "stored-key");

        Assert.Equal(ProviderStatusKind.Ready, catalog.Find("keyed")!.Status.Kind);
        Assert.Equal(ProviderStatusKind.NeedsKey, catalog.Find("bare")!.Status.Kind);
    }

    [Fact]
    public void A_provider_without_a_base_url_is_an_error_and_a_sign_in_without_a_login_is_not_ready()
    {
        var noUrl = Definition("no-url", ProviderCredentialKind.ApiKey) with { BaseUrl = string.Empty };
        var copilot = Definition("copilot", ProviderCredentialKind.GitHubCopilot);
        var catalog = ProviderCatalog.Create([noUrl, copilot], "no-url", secrets, credentials);

        Assert.Equal(ProviderStatusKind.Error, catalog.Find("no-url")!.Status.Kind);
        Assert.Equal(ProviderStatusKind.NeedsSignIn, catalog.Find("copilot")!.Status.Kind);
    }

    [Fact]
    public void The_environment_variable_wins_over_a_stored_key()
    {
        var definition = Definition("deepseek", ProviderCredentialKind.ApiKey) with
        {
            EnvironmentVariable = "FORGE_TEST_DEEPSEEK_KEY",
        };
        var catalog = ProviderCatalog.Create([definition], "deepseek", secrets, credentials);
        secrets.Set(catalog.Find("deepseek")!.Definition.SecretReference!, "stored-old-key");
        var previous = Environment.GetEnvironmentVariable("FORGE_TEST_DEEPSEEK_KEY");
        Environment.SetEnvironmentVariable("FORGE_TEST_DEEPSEEK_KEY", "from-environment");
        try
        {
            var provider = catalog.Find("deepseek")!;

            Assert.Equal("from-environment", provider.ResolveApiKey());
            Assert.True(provider.KeyComesFromEnvironment);
            Assert.Equal(ProviderStatusKind.Ready, provider.Status.Kind);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FORGE_TEST_DEEPSEEK_KEY", previous);
        }
    }

    [Fact]
    public void The_global_window_overrides_the_catalog_and_a_per_model_window_still_wins()
    {
        var provider = Definition("windowed", ProviderCredentialKind.ApiKey) with
        {
            Models = ["with-window", "with-prices-only", "plain"],
            Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal)
            {
                ["with-window"] = new(300_000, null),
                ["with-prices-only"] = new(null, new ModelPricing(1, 2, 0.1, 0)),
            },
        };
        var catalog = ProviderCatalog.Create([provider], "windowed", secrets, credentials, cache: null, defaultContextWindow: 64_000);

        Assert.Equal(300_000, catalog.MetadataFor("with-window", "windowed").ContextWindow);
        // A prices-only override must not drop the global limit.
        Assert.Equal(64_000, catalog.MetadataFor("with-prices-only", "windowed").ContextWindow);
        Assert.Equal(new ModelPricing(1, 2, 0.1, 0), catalog.MetadataFor("with-prices-only", "windowed").Pricing);
        // No override at all: the global value replaces whatever the table knows.
        Assert.Equal(64_000, catalog.MetadataFor("plain", "windowed").ContextWindow);
    }

    [Fact]
    public void Without_a_global_window_the_catalog_value_is_used()
    {
        var provider = Definition("plain", ProviderCredentialKind.ApiKey);
        var catalog = ProviderCatalog.Create([provider], "plain", secrets, credentials);

        Assert.Null(catalog.DefaultContextWindow);
        Assert.Equal(ModelMetadataResolver.Resolve("plain", null, null).ContextWindow, catalog.MetadataFor("plain").ContextWindow);
    }

    [Fact]
    public void Metadata_asked_with_a_provider_id_uses_that_provider()
    {
        var alpha = Definition("alpha", ProviderCredentialKind.ApiKey) with
        {
            Models = ["shared"],
            Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal) { ["shared"] = new(1_000, null) },
        };
        var beta = Definition("beta", ProviderCredentialKind.ApiKey) with
        {
            Models = ["shared"],
            Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal) { ["shared"] = new(2_000, null) },
        };
        var catalog = ProviderCatalog.Create([alpha, beta], "alpha", secrets, credentials);

        Assert.Equal(1_000, catalog.MetadataFor("shared").ContextWindow);
        Assert.Equal(2_000, catalog.MetadataFor("shared", "beta").ContextWindow);
        Assert.Equal(1_000, catalog.MetadataFor("shared", "unknown-provider").ContextWindow);
    }

    [Fact]
    public void A_reported_model_list_keeps_the_server_order_in_the_menu()
    {
        var catalog = Catalog(("ordered", ProviderCredentialKind.ApiKey));
        catalog.Models.Set(
            "ordered",
            new ProviderModelList(
                ["model-a", "model-b", "model-c"],
                new Dictionary<string, ModelMetadata>(StringComparer.Ordinal)
                {
                    ["model-c"] = new(4096, null),
                }));

        Assert.Equal(["model-a", "model-b", "model-c"], catalog.Find("ordered")!.Models);
    }

    [Fact]
    public void Metadata_for_an_ambiguous_model_prefers_the_default_provider_then_the_first_provider_that_knows_it()
    {
        var first = Definition("first", ProviderCredentialKind.ApiKey) with
        {
            Models = ["shared", "first-only"],
            Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal)
            {
                ["shared"] = new(1_000, null),
                ["first-only"] = new(2_000, null),
            },
        };
        var second = Definition("second", ProviderCredentialKind.ApiKey) with
        {
            Models = ["shared"],
            Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal)
            {
                ["shared"] = new(9_000, null),
            },
        };
        var catalog = ProviderCatalog.Create([first, second], "second", secrets, credentials);

        Assert.Equal(9_000, catalog.MetadataFor("shared").ContextWindow);
        Assert.Equal(2_000, catalog.MetadataFor("first-only").ContextWindow);
        Assert.False(catalog.MetadataFor("nobody-serves-this").IsKnown);
    }

    [Fact]
    public async Task An_empty_catalogue_creates_a_provider_that_reports_the_missing_configuration()
    {
        var catalog = ProviderCatalog.Empty(ProviderStrings.SectionMissingText());
        var provider = catalog.CreateProvider(new ModelSettings("m"), new HttpClient());

        Assert.False(catalog.IsConfigured);
        var exception = await Assert.ThrowsAsync<ProviderException>(() => ConsumeAsync(provider));
        Assert.False(exception.Retryable);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async Task ConsumeAsync(IChatProvider provider)
    {
        await using var enumerator = provider
            .StreamAsync(new ChatRequest(string.Empty, [], [], new ModelSettings("m")), CancellationToken.None)
            .GetAsyncEnumerator();
        await enumerator.MoveNextAsync();
    }

    private ProviderCatalog Catalog(params (string Id, ProviderCredentialKind Kind)[] providers) =>
        Catalog(providers, defaultId: providers[0].Id);

    private ProviderCatalog Catalog((string Id, ProviderCredentialKind Kind)[] providers, string defaultId) =>
        ProviderCatalog.Create(
            [.. providers.Select(provider => Definition(provider.Id, provider.Kind))],
            defaultId,
            secrets,
            credentials);

    private ProviderDefinition Definition(string id, ProviderCredentialKind kind) => new()
    {
        Id = id,
        PresetId = ProviderPresets.CustomId,
        DisplayName = id,
        Protocol = ProviderProtocol.OpenAiCompatible,
        BaseUrl = "https://example.invalid/v1",
        CredentialKind = kind,
        EnvironmentVariable = null,
        SecretReference = "provider:" + id,
        Models = ["m"],
        DefaultModel = "m",
        Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
    };
}

/// <summary>
/// The two editors are pure view models: validation, what the definition does and does not carry (never the secret), and the
/// metadata rows that must not turn "unknown" into zero.
/// </summary>
public sealed class ProviderEditorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-provider-editor-" + Guid.NewGuid().ToString("N"));

    public ProviderEditorTests() => Directory.CreateDirectory(root);

    [Fact]
    public void An_incomplete_form_reports_why_it_cannot_be_saved()
    {
        var services = Services();
        var editor = new ProviderEditorViewModel(services, definition: null, isDefault: false, ProviderPresets.Custom);

        editor.Name = "  ";
        Assert.Null(editor.TryBuild());
        Assert.NotNull(editor.Error);

        editor.Name = "Gateway";
        editor.BaseUrl = "not a url";
        Assert.Null(editor.TryBuild());
        Assert.NotNull(editor.Error);

        editor.BaseUrl = "https://example.invalid/v1";
        editor.Models.Clear();
        editor.DefaultModel = string.Empty;
        Assert.Null(editor.TryBuild());
        Assert.NotNull(editor.Error);

        editor.AddModel("gpt-x");
        var definition = editor.TryBuild();
        Assert.NotNull(definition);
        Assert.Equal("gpt-x", definition!.DefaultModel);
        Assert.Null(editor.Error);
    }

    [Fact]
    public void A_valid_form_carries_only_a_reference_and_never_the_secret()
    {
        var services = Services();
        var editor = new ProviderEditorViewModel(services, definition: null, isDefault: true, ProviderPresets.Custom)
        {
            Name = "Company gateway",
            BaseUrl = "https://gateway.invalid/v1",
            Secret = "sk-live-not-a-real-key",
        };
        editor.AddModel("internal-model");

        var definition = editor.TryBuild();

        Assert.NotNull(definition);
        Assert.Equal("provider:company-gateway", definition!.SecretReference);
        var stored = JsonSerializer.Serialize(
            definition.ToDocument(),
            ProvidersJsonContext.Default.ProviderEntryDocument);
        Assert.DoesNotContain("sk-live-not-a-real-key", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_provider_with_the_same_name_gets_its_own_id()
    {
        var services = Services();
        string[] existing = ["openai"];

        var editor = new ProviderEditorViewModel(
            services,
            definition: null,
            isDefault: false,
            ProviderPresets.Find(ProviderPresets.OpenAiId),
            existing)
        {
            Name = "OpenAI",
            BaseUrl = "https://api.openai.com/v1",
        };
        editor.AddModel("gpt-x");

        var definition = editor.TryBuild();

        Assert.NotNull(definition);
        Assert.Equal("openai-2", definition!.Id);
    }

    [Fact]
    public void Removing_the_default_model_moves_the_default_to_the_next_one()
    {
        var services = Services();
        var editor = new ProviderEditorViewModel(services, definition: null, isDefault: true, ProviderPresets.Custom);
        editor.Name = "Gateway";
        editor.BaseUrl = "https://example.invalid/v1";
        editor.AddModel("first");
        editor.AddModel("second");
        editor.DefaultModel = "first";

        editor.RemoveModel("first");

        Assert.Equal(["second"], editor.Models);
        Assert.Equal("second", editor.DefaultModel);
    }

    [Fact]
    public void The_cache_write_column_only_appears_where_it_is_billed()
    {
        var services = Services();
        var openAi = OpenAICompatible();
        var anthropic = AnthropicCompatible();

        Assert.False(new ModelMetadataViewModel(services, services.CreateTransient(openAi)).ShowsCacheWriteColumn);
        Assert.True(new ModelMetadataViewModel(services, services.CreateTransient(anthropic)).ShowsCacheWriteColumn);

        // An OpenAI-compatible gateway that does bill cache writes keeps the column once a value exists.
        var withWritePrice = openAi with
        {
            Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal)
            {
                ["m"] = new(null, new ModelPricing(1, 2, 0.1, 0.5)),
            },
        };
        Assert.True(new ModelMetadataViewModel(services, services.CreateTransient(withWritePrice)).ShowsCacheWriteColumn);
    }

    [Fact]
    public async Task An_invalid_global_window_is_reported_and_not_saved()
    {
        var services = Services();
        using var section = new ProvidersSettingsViewModel(services);
        section.Reload();
        section.DefaultContextWindowText = "not a number";

        await section.CommitDefaultContextWindowAsync();

        Assert.True(section.HasProblem);
        Assert.Null(services.Catalog.DefaultContextWindow);
        Assert.False(File.Exists(Path.Combine(root, "settings.json")), "nothing is written for an invalid value");

        section.DefaultContextWindowText = "64000";
        await section.CommitDefaultContextWindowAsync();

        Assert.Equal(64_000, services.Catalog.DefaultContextWindow);
        Assert.Contains("64000", File.ReadAllText(Path.Combine(root, "settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_rows_never_write_a_zero_for_an_unknown_half()
    {
        var rows = new[]
        {
            new ModelMetadataRowViewModel("empty", ModelMetadata.Unknown),
            new ModelMetadataRowViewModel("window", new ModelMetadata(128_000, null)),
            new ModelMetadataRowViewModel("priced", new ModelMetadata(null, new ModelPricing(1, 2, 0, 0))),
        };

        var overrides = ModelMetadataViewModel.ToOverrides(rows);

        Assert.False(overrides.ContainsKey("empty"));
        Assert.Equal(128_000, overrides["window"].ContextWindow);
        Assert.Null(overrides["window"].Input);
        Assert.Equal(1, overrides["priced"].Input);
        Assert.Equal(2, overrides["priced"].Output);
        Assert.Null(overrides["priced"].ContextWindow);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private ProviderServices Services() =>
        new(Path.Combine(root, "settings.json"), root, new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private static ProviderDefinition OpenAICompatible() => new()
    {
        Id = "gateway",
        PresetId = ProviderPresets.CustomId,
        DisplayName = "Gateway",
        Protocol = ProviderProtocol.OpenAiCompatible,
        BaseUrl = "https://gateway.invalid/v1",
        CredentialKind = ProviderCredentialKind.ApiKey,
        SecretReference = "provider:gateway",
        Models = ["m"],
        DefaultModel = "m",
        Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
    };

    private static ProviderDefinition AnthropicCompatible() => OpenAICompatible() with
    {
        Id = "claude-gateway",
        Protocol = ProviderProtocol.Anthropic,
        BaseUrl = "https://claude.invalid",
        SecretReference = "provider:claude-gateway",
    };
}
