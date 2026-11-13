using System.Net;
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
    private readonly string root = TestTempFiles.NewDirectory("forge-provider-catalog-");
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
        var catalog = ProviderCatalog.Create([provider], "windowed", secrets, credentials, cache: null, new ProviderCatalogOptions(DefaultContextWindow: 64_000));

        Assert.Equal(300_000, catalog.MetadataFor("with-window", "windowed").ContextWindow);
        // A prices-only override must not drop the global limit.
        Assert.Equal(64_000, catalog.MetadataFor("with-prices-only", "windowed").ContextWindow);
        Assert.Equal(new ModelPricing(1, 2, 0.1, 0), catalog.MetadataFor("with-prices-only", "windowed").Pricing);
        // No override at all: the global value replaces whatever the table knows.
        Assert.Equal(64_000, catalog.MetadataFor("plain", "windowed").ContextWindow);
    }

    [Fact]
    public void A_share_window_scales_the_window_the_chain_already_knows()
    {
        var provider = Definition("shared", ProviderCredentialKind.ApiKey) with
        {
            Models = ["overridden", "reported", "priced-only", "nobody-knows"],
            Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal)
            {
                ["overridden"] = new(300_000, null),
                ["priced-only"] = new(null, new ModelPricing(1, 2, 0.1, 0)),
            },
        };
        var catalog = ProviderCatalog.Create(
            [provider],
            "shared",
            secrets,
            credentials,
            cache: null,
            new ProviderCatalogOptions(DefaultContextWindow: ContextWindowDefault.FromPercent(50)));
        catalog.Models.Set(
            "shared",
            new ProviderModelList(
                ["reported"],
                new Dictionary<string, ModelMetadata>(StringComparer.Ordinal) { ["reported"] = new(128_000, null) }));

        // A per-model window (and a window the provider reported) is the base of the share, not something it overrides.
        Assert.Equal(150_000, catalog.MetadataFor("overridden", "shared").ContextWindow);
        Assert.Equal(64_000, catalog.MetadataFor("reported", "shared").ContextWindow);

        // A prices-only override still does not block the global setting, but it also has no window for the share to scale.
        Assert.Null(catalog.MetadataFor("priced-only", "shared").ContextWindow);
        Assert.Equal(new ModelPricing(1, 2, 0.1, 0), catalog.MetadataFor("priced-only", "shared").Pricing);

        // No layer knows a window: the share contributes nothing and the model stays unknown instead of getting a guess.
        Assert.Null(catalog.MetadataFor("nobody-knows", "shared").ContextWindow);
        Assert.False(catalog.MetadataFor("nobody-knows", "shared").IsKnown);
        Assert.Null(catalog.MetadataFor("no-provider-knows-this").ContextWindow);
    }

    [Fact]
    public void A_share_window_is_clamped_to_one_token_and_may_exceed_one_hundred_percent()
    {
        var provider = Definition("scaled", ProviderCredentialKind.ApiKey) with { Models = ["tiny", "large"] };

        ProviderCatalog WithShare(int percent) => ProviderCatalog.Create(
            [provider],
            "scaled",
            secrets,
            credentials,
            cache: null,
            new ProviderCatalogOptions(DefaultContextWindow: ContextWindowDefault.FromPercent(percent)));

        // 1% of 100 tokens floors to 1 rather than to zero tokens.
        var tiny = WithShare(1);
        tiny.Models.Set("scaled", new ProviderModelList(["tiny"], new Dictionary<string, ModelMetadata>(StringComparer.Ordinal) { ["tiny"] = new(100, null) }));
        Assert.Equal(1, tiny.MetadataFor("tiny", "scaled").ContextWindow);

        // Above 100% is allowed on purpose: a provider may permit a larger window than the one it advertises.
        var large = WithShare(200);
        large.Models.Set("scaled", new ProviderModelList(["large"], new Dictionary<string, ModelMetadata>(StringComparer.Ordinal) { ["large"] = new(64_000, null) }));
        Assert.Equal(128_000, large.MetadataFor("large", "scaled").ContextWindow);
    }

    [Fact]
    public void Without_a_global_window_the_catalog_value_is_used()
    {
        var provider = Definition("plain", ProviderCredentialKind.ApiKey);
        var catalog = ProviderCatalog.Create([provider], "plain", secrets, credentials);

        Assert.True(catalog.DefaultContextWindow.IsEmpty);
        Assert.Equal(ModelMetadataResolver.Resolve("plain", null, null, null).ContextWindow, catalog.MetadataFor("plain").ContextWindow);
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
        TestTempFiles.DeleteQuietly(root);
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
    private readonly string root = TestTempFiles.NewDirectory("forge-provider-editor-");


    [Fact]
    public void An_incomplete_form_reports_why_it_cannot_be_saved()
    {
        var services = Services();
        var editor = new ProviderEditorViewModel(services, definition: null, ProviderPresets.Custom);

        editor.Name = "  ";
        Assert.Null(editor.TryBuild());
        Assert.NotNull(editor.Error);

        editor.Name = "Gateway";
        editor.BaseUrl = "not a url";
        Assert.Null(editor.TryBuild());
        Assert.NotNull(editor.Error);

        editor.BaseUrl = "https://example.invalid/v1";
        Assert.NotNull(editor.TryBuild());
        Assert.Null(editor.Error);

        // The environment-variable mode needs a name to read from; an empty box is a mistake, not "no key".
        editor.KeyIsSecret = false;
        editor.KeyText = " ";
        Assert.Null(editor.TryBuild());
        Assert.NotNull(editor.Error);
    }

    [Fact]
    public void A_valid_form_carries_only_a_reference_and_never_the_secret()
    {
        var services = Services();
        var editor = new ProviderEditorViewModel(services, definition: null, ProviderPresets.Custom)
        {
            Name = "Company gateway",
            BaseUrl = "https://gateway.invalid/v1",
            KeyText = "sk-live-not-a-real-key",
        };

        var definition = editor.TryBuild();

        Assert.NotNull(definition);
        Assert.Equal("provider:company-gateway", definition!.SecretReference);
        Assert.Null(definition.EnvironmentVariable);
        Assert.Equal(ThinkingEffort.Medium, definition.Thinking);
        var stored = JsonSerializer.Serialize(
            definition.ToDocument(),
            ProvidersJsonContext.Default.ProviderEntryDocument);
        Assert.DoesNotContain("sk-live-not-a-real-key", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void Environment_mode_stores_the_variable_name_and_switching_back_clears_it()
    {
        var services = Services();
        var editor = new ProviderEditorViewModel(services, definition: null, ProviderPresets.Find(ProviderPresets.OpenAiId));
        editor.Name = "OpenAI";

        editor.KeyIsSecret = false;
        Assert.Equal("OPENAI_API_KEY", editor.KeyText);

        var definition = editor.TryBuild();
        Assert.NotNull(definition);
        Assert.Equal("OPENAI_API_KEY", definition!.EnvironmentVariable);

        editor.KeyIsSecret = true;
        Assert.Equal(string.Empty, editor.KeyText);
    }

    [Fact]
    public void Request_headers_round_trip_through_the_box_and_the_stored_document()
    {
        var services = Services();
        var editor = new ProviderEditorViewModel(services, definition: null, ProviderPresets.Custom)
        {
            Name = "OpenCode Go",
            BaseUrl = "https://opencode.ai/zen/go/v1",
            HeadersText = "x-opencode-session: {session}\n\n  X-Mode :  a:b  \r\nx-mode: last",
        };

        var definition = editor.TryBuild();

        Assert.NotNull(definition);
        Assert.Equal(2, definition!.Headers.Count);
        Assert.Equal("{session}", definition.Headers["x-opencode-session"]);
        // Split at the first colon; names are case-insensitive and the later line wins.
        Assert.Equal("last", definition.Headers["X-MODE"]);

        var reloaded = ProviderDefinition.FromDocument(definition.ToDocument(), definition.Id);
        Assert.Equal("{session}", reloaded.Headers["x-opencode-session"]);

        var reopened = new ProviderEditorViewModel(services, reloaded);
        Assert.Contains("x-opencode-session: {session}", reopened.HeadersText, StringComparison.Ordinal);
        Assert.Equal(definition.Headers, reopened.TryBuild()!.Headers);
    }

    [Theory]
    [InlineData("no colon here", 1)]
    [InlineData("x-ok: 1\n: value", 2)]
    [InlineData("x-ok: 1\n\nx-empty:   ", 3)]
    [InlineData("bad name: v", 1)]
    public void A_malformed_header_line_is_refused_with_its_number(string text, int line)
    {
        var services = Services();
        var editor = new ProviderEditorViewModel(services, definition: null, ProviderPresets.Custom)
        {
            Name = "Gateway",
            BaseUrl = "https://gateway.invalid/v1",
            HeadersText = text,
        };

        Assert.Null(editor.TryBuild());
        Assert.Contains(line.ToString(System.Globalization.CultureInfo.InvariantCulture), editor.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Editing_the_headers_asks_for_a_new_test()
    {
        var services = Services();
        var existing = new ProviderEditorViewModel(services, definition: null, ProviderPresets.Custom)
        {
            Name = "Gateway",
            BaseUrl = "https://gateway.invalid/v1",
        }.TryBuild()!;
        var editor = new ProviderEditorViewModel(services, existing);
        Assert.Equal(ProviderTestState.Passed, editor.TestState);

        editor.HeadersText = "x-opencode-session: {session}";

        Assert.Equal(ProviderTestState.Untested, editor.TestState);
    }

    [Fact]
    public void A_second_provider_with_the_same_name_gets_its_own_id()
    {
        var services = Services();
        string[] existing = ["openai"];

        var editor = new ProviderEditorViewModel(
            services,
            definition: null,
            ProviderPresets.Find(ProviderPresets.OpenAiId),
            existing)
        {
            Name = "OpenAI",
        };

        var definition = editor.TryBuild();

        Assert.NotNull(definition);
        Assert.Equal("openai-2", definition!.Id);
    }

    [Fact]
    public async Task A_passing_test_fetches_the_models_and_the_save_writes_them()
    {
        var services = ServicesWith(new FixedHandler(HttpStatusCode.OK, """{"data":[{"id":"m1"},{"id":"m2"}]}"""));
        var editor = new ProviderEditorViewModel(services, definition: null, ProviderPresets.Custom)
        {
            Name = "Gateway",
            BaseUrl = "https://gateway.invalid/v1",
            KeyText = "sk-test",
        };
        ProviderDefinition? saved = null;
        editor.Saved += (_, definition) => saved = definition;
        Assert.Equal(ProviderTestState.Untested, editor.TestState);

        await editor.TestOrSaveAsync();

        Assert.Equal(ProviderTestState.Passed, editor.TestState);
        Assert.Equal("保存", editor.TestOrSaveText);
        Assert.Null(saved);

        await editor.TestOrSaveAsync();

        Assert.NotNull(saved);
        Assert.Equal(["m1", "m2"], saved!.Models);
        Assert.Equal("m1", saved.DefaultModel);
        Assert.Equal("sk-test", services.Secrets.Get(saved.SecretReference!));
    }

    [Fact]
    public async Task A_failed_test_keeps_the_test_button_and_reports_the_reason()
    {
        var services = ServicesWith(new FixedHandler(HttpStatusCode.Unauthorized, """{"error":"bad key"}"""));
        var editor = new ProviderEditorViewModel(services, definition: null, ProviderPresets.Custom)
        {
            Name = "Gateway",
            BaseUrl = "https://gateway.invalid/v1",
            KeyText = "sk-wrong",
        };

        await editor.TestOrSaveAsync();

        Assert.Equal(ProviderTestState.Untested, editor.TestState);
        Assert.Equal("测试连接", editor.TestOrSaveText);
        Assert.True(editor.HasError);
    }

    [Fact]
    public async Task Cancelling_after_a_test_removes_the_draft_key_and_never_writes_the_real_one()
    {
        var services = ServicesWith(new FixedHandler(HttpStatusCode.OK, """{"data":[]}"""));
        var editor = new ProviderEditorViewModel(services, definition: null, ProviderPresets.Custom)
        {
            Name = "Throwaway",
            BaseUrl = "https://gateway.invalid/v1",
            KeyText = "sk-orphan-candidate",
        };
        await editor.TestOrSaveAsync();

        Assert.True(services.Secrets.Has("provider-draft:throwaway"), "the test reads the typed key from a draft reference");
        Assert.False(services.Secrets.Has("provider:throwaway"), "a test never writes the provider's real reference");

        await editor.AbandonAsync();

        Assert.False(services.Secrets.Has("provider-draft:throwaway"));
        Assert.False(services.Secrets.Has("provider:throwaway"));
    }

    [Fact]
    public async Task A_test_on_an_existing_provider_leaves_its_stored_key_until_the_save_succeeds()
    {
        var services = ServicesWith(new FixedHandler(HttpStatusCode.OK, """{"data":[{"id":"m1"}]}"""));
        services.Secrets.Set("provider:gateway", "sk-old");
        var definition = OpenAICompatible() with { EnvironmentVariable = null };
        var editor = new ProviderEditorViewModel(services, definition, persist: _ => Task.FromResult<string?>(null));
        editor.KeyText = "sk-new";
        ProviderDefinition? saved = null;
        editor.Saved += (_, value) => saved = value;

        await editor.TestOrSaveAsync();
        Assert.Equal("sk-old", services.Secrets.Get("provider:gateway"));
        Assert.Equal(ProviderTestState.Passed, editor.TestState);

        await editor.TestOrSaveAsync();
        Assert.NotNull(saved);
        Assert.Equal("sk-new", services.Secrets.Get("provider:gateway"));
        Assert.False(services.Secrets.Has("provider-draft:gateway"));
    }

    [Fact]
    public async Task A_refused_settings_write_keeps_the_dialog_open_and_the_old_key()
    {
        var services = ServicesWith(new FixedHandler(HttpStatusCode.OK, """{"data":[{"id":"m1"}]}"""));
        services.Secrets.Set("provider:gateway", "sk-old");
        var editor = new ProviderEditorViewModel(
            services,
            OpenAICompatible() with { EnvironmentVariable = null },
            persist: _ => Task.FromResult<string?>("settings.json is read-only"));
        editor.KeyText = "sk-new";
        ProviderDefinition? saved = null;
        editor.Saved += (_, value) => saved = value;

        await editor.TestOrSaveAsync();
        await editor.TestOrSaveAsync();

        Assert.Null(saved);
        Assert.Equal("settings.json is read-only", editor.Error);
        Assert.Equal(ProviderTestState.Passed, editor.TestState);
        Assert.Equal("sk-old", services.Secrets.Get("provider:gateway"));
    }

    [Fact]
    public void Editing_a_passing_provider_saves_directly_until_the_address_changes()
    {
        var services = Services();
        var definition = OpenAICompatible();
        var editor = new ProviderEditorViewModel(services, definition);
        ProviderDefinition? saved = null;
        editor.Saved += (_, value) => saved = value;

        Assert.Equal(ProviderTestState.Passed, editor.TestState);
        Assert.Equal("保存", editor.TestOrSaveText);

        editor.Name = "Renamed";
        editor.TestOrSaveCommand.Execute(null);
        Assert.Equal("Renamed", saved?.DisplayName);

        saved = null;
        editor.BaseUrl = "https://elsewhere.invalid/v1";
        Assert.Equal(ProviderTestState.Untested, editor.TestState);
        Assert.Equal("测试连接", editor.TestOrSaveText);
        editor.TestOrSaveCommand.Execute(null);
        Assert.Null(saved);
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
        Assert.True(services.Catalog.DefaultContextWindow.IsEmpty);
        Assert.False(File.Exists(Path.Combine(root, "settings.json")), "nothing is written for an invalid value");

        section.DefaultContextWindowText = "64000";
        await section.CommitDefaultContextWindowAsync();

        Assert.Equal(ContextWindowDefault.FromTokens(64_000), services.Catalog.DefaultContextWindow);
        Assert.Contains("64000", File.ReadAllText(Path.Combine(root, "settings.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_share_typed_in_the_section_is_accepted_and_saved_as_a_string()
    {
        var services = Services();
        using var section = new ProvidersSettingsViewModel(services);
        section.Reload();
        // "50 %" with the space is the same share as "50%": a typo, not a different meaning.
        section.DefaultContextWindowText = "50 %";

        await section.CommitDefaultContextWindowAsync();

        Assert.False(section.HasProblem);
        Assert.Equal(ContextWindowDefault.FromPercent(50), services.Catalog.DefaultContextWindow);
        Assert.Contains("\"50%\"", File.ReadAllText(Path.Combine(root, "settings.json")), StringComparison.Ordinal);

        // The box shows the stored value back in the shape it was entered.
        section.Reload();
        Assert.Equal("50%", section.DefaultContextWindowText);
    }

    [Theory]
    [InlineData("0%")]
    [InlineData("abc")]
    [InlineData("%")]
    [InlineData("-5")]
    [InlineData("1001%")]
    public async Task A_rejected_global_window_is_reported_with_the_invalid_message_and_nothing_is_written(string text)
    {
        var services = Services();
        using var section = new ProvidersSettingsViewModel(services);
        section.Reload();
        section.DefaultContextWindowText = text;

        await section.CommitDefaultContextWindowAsync();

        Assert.Equal(Loc.Instance[ProviderStrings.DefaultContextWindowInvalidKey], section.Problem);
        Assert.True(services.Catalog.DefaultContextWindow.IsEmpty);
        Assert.False(File.Exists(Path.Combine(root, "settings.json")), "nothing is written for an invalid value");
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
        TestTempFiles.DeleteQuietly(root);
    }

    private ProviderServices Services() =>
        new(Path.Combine(root, "settings.json"), root, new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private ProviderServices ServicesWith(HttpMessageHandler handler) =>
        new(Path.Combine(root, "settings.json"), root, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });

    /// <summary>Answers every request with one status and body; the test connection only ever asks <c>/models</c>.</summary>
    private sealed class FixedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }

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
