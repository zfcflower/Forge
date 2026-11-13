using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Providers;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// <see cref="ProviderStore"/> owns the file rules: the legacy section migrates once, a save rewrites only what it owns,
/// and a damaged file is never overwritten. Every test works on a throw-away directory.
/// </summary>
public sealed class ProviderStoreTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-provider-store-");


    [Fact]
    public void The_legacy_single_provider_section_loads_as_one_connected_provider()
    {
        var path = WriteSettings(
            """
            {
              "provider": {
                "kind": "openai-compatible",
                "baseUrl": "https://api.deepseek.com/v1",
                "model": "deepseek-chat",
                "models": ["deepseek-reasoner"],
                "apiKeyEnv": "DEEPSEEK_API_KEY",
                "modelMeta": { "deepseek-chat": { "contextWindow": 64000, "input": 0.14 } }
              }
            }
            """);
        var (store, secrets, credentials) = CreateStack(path);

        var catalog = store.Load(secrets, credentials);

        var provider = Assert.Single(catalog.Providers);
        Assert.Equal("https://api.deepseek.com/v1", provider.Definition.BaseUrl);
        Assert.Equal(["deepseek-chat", "deepseek-reasoner"], provider.Definition.Models);
        Assert.Equal("DEEPSEEK_API_KEY", provider.Definition.EnvironmentVariable);
        Assert.Equal(64_000, provider.Definition.Overrides["deepseek-chat"].ContextWindow);
        Assert.Same(provider, catalog.Default);
    }

    [Fact]
    public void The_legacy_section_migrates_to_the_protocol_and_step_it_names()
    {
        // The migration reads the section as the current schema: the three protocols by name and every thinking step this
        // build knows, so nothing silently degrades to a guess.
        var path = WriteSettings(
            """
            {
              "provider": {
                "kind": "openai-responses",
                "baseUrl": "https://api.meta.ai/v1",
                "model": "muse-code",
                "thinking": "xhigh"
              }
            }
            """);
        var (store, secrets, credentials) = CreateStack(path);

        var provider = Assert.Single(store.Load(secrets, credentials).Providers);

        Assert.Equal(ProviderProtocol.OpenAiResponses, provider.Definition.Protocol);
        Assert.Equal(ThinkingEffort.XHigh, provider.Definition.Thinking);
        Assert.Equal(ProviderCredentialKind.ApiKey, provider.Definition.CredentialKind);
    }

    [Fact]
    public void A_legacy_kind_this_build_does_not_speak_is_unusable_rather_than_guessed()
    {
        var path = WriteSettings(
            """
            {
              "provider": {
                "kind": "grpc-stream",
                "baseUrl": "https://example.invalid",
                "model": "whatever"
              }
            }
            """);
        var (store, secrets, credentials) = CreateStack(path);

        var catalog = store.Load(secrets, credentials);

        Assert.Empty(catalog.Providers);
        Assert.False(catalog.IsConfigured);
    }

    [Fact]
    public void The_first_save_writes_providers_and_drops_the_legacy_section_but_keeps_unknown_fields()
    {
        var path = WriteSettings(
            """
            {
              "provider": { "kind": "anthropic", "baseUrl": "https://api.anthropic.com", "model": "claude-sonnet-4-5" },
              "preferences": { "language": "english" },
              "somethingThisBuildDoesNotKnow": { "keep": true }
            }
            """);
        var (store, secrets, credentials) = CreateStack(path);
        var catalog = store.Load(secrets, credentials);

        store.Save([.. catalog.Providers.Select(provider => provider.Definition)], catalog.Default?.Id, defaultContextWindow: null);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(document.RootElement.TryGetProperty("provider", out _), "the migrated section is gone after the first save");
        Assert.True(document.RootElement.TryGetProperty("providers", out var providers));
        Assert.Equal(1, providers.GetProperty("version").GetInt32());
        Assert.Equal("anthropic", providers.GetProperty("defaultId").GetString());
        Assert.Equal(1, providers.GetProperty("items").GetArrayLength());
        Assert.Equal("english", document.RootElement.GetProperty("preferences").GetProperty("language").GetString());
        Assert.True(document.RootElement.GetProperty("somethingThisBuildDoesNotKnow").GetProperty("keep").GetBoolean());
    }

    [Fact]
    public void A_save_over_an_invalid_file_refuses_and_leaves_it_untouched()
    {
        const string broken = "{ not json";
        var path = WriteSettings(broken);
        var (store, _, _) = CreateStack(path);

        Assert.Throws<InvalidOperationException>(() => store.Save([], defaultId: null, defaultContextWindow: null));
        Assert.Equal(broken, File.ReadAllText(path));
    }

    [Fact]
    public void The_global_context_window_round_trips_and_leaves_unknown_catalog_fields_alone()
    {
        var path = WriteSettings(
            """
            {
              "providers": { "version": 1, "defaultId": "only", "items": [
                { "id": "only", "preset": "openai", "baseUrl": "https://api.openai.com/v1", "auth": { "kind": "api-key" },
                  "models": ["gpt"], "defaultModel": "gpt" } ] },
              "modelCatalog": { "generatedAt": "2026-11-01T00:00:00Z", "defaultContextWindow": 128000 }
            }
            """);
        var (store, secrets, credentials) = CreateStack(path);

        var catalog = store.Load(secrets, credentials);
        Assert.Equal(ContextWindowDefault.FromTokens(128_000), catalog.DefaultContextWindow);

        store.Save([.. catalog.Providers.Select(provider => provider.Definition)], "only", defaultContextWindow: 200_000);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var section = document.RootElement.GetProperty("modelCatalog");
        Assert.Equal(200_000, section.GetProperty("defaultContextWindow").GetInt32());
        Assert.Equal("2026-11-01T00:00:00Z", section.GetProperty("generatedAt").GetString());

        // Clearing the value removes only that property; the section and its other fields stay.
        store.Save([.. catalog.Providers.Select(provider => provider.Definition)], "only", defaultContextWindow: null);
        using var cleared = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(cleared.RootElement.GetProperty("modelCatalog").TryGetProperty("defaultContextWindow", out _));
        Assert.True(cleared.RootElement.GetProperty("modelCatalog").TryGetProperty("generatedAt", out _));
    }

    [Fact]
    public void The_global_context_window_round_trips_a_count_as_a_number_and_a_share_as_a_string()
    {
        var path = WriteSettings(
            """
            {
              "providers": { "version": 1, "defaultId": "only", "items": [
                { "id": "only", "preset": "openai", "baseUrl": "https://api.openai.com/v1", "auth": { "kind": "api-key" },
                  "models": ["gpt"], "defaultModel": "gpt" } ] },
              "modelCatalog": {
                "defaultContextWindow": 64000,
                "generatedAt": "2026-11-01T00:00:00Z",
                "sourceUrl": "https://mirror.invalid/api.json",
                "somethingThisBuildDoesNotKnow": { "keep": true }
              }
            }
            """);
        var (store, secrets, credentials) = CreateStack(path);
        var catalog = store.Load(secrets, credentials);
        var definitions = catalog.Providers.Select(provider => provider.Definition).ToList();

        // An absolute count stays a JSON number, so a build from before shares existed reads it unchanged.
        store.Save(definitions, "only", ContextWindowDefault.FromTokens(128_000), catalog.ModelCatalogSourceUrl);
        using (var count = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var section = count.RootElement.GetProperty("modelCatalog");
            Assert.Equal(JsonValueKind.Number, section.GetProperty("defaultContextWindow").ValueKind);
            Assert.Equal(128_000, section.GetProperty("defaultContextWindow").GetInt32());
            Assert.True(section.GetProperty("somethingThisBuildDoesNotKnow").GetProperty("keep").GetBoolean());
        }

        // A share is a string under the same one property, and it reads back as a share.
        store.Save(definitions, "only", ContextWindowDefault.FromPercent(50), catalog.ModelCatalogSourceUrl);
        using (var share = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var section = share.RootElement.GetProperty("modelCatalog");
            Assert.Equal(JsonValueKind.String, section.GetProperty("defaultContextWindow").ValueKind);
            Assert.Equal("50%", section.GetProperty("defaultContextWindow").GetString());
            Assert.Equal("2026-11-01T00:00:00Z", section.GetProperty("generatedAt").GetString());
            Assert.Equal("https://mirror.invalid/api.json", section.GetProperty("sourceUrl").GetString());
            Assert.True(section.GetProperty("somethingThisBuildDoesNotKnow").GetProperty("keep").GetBoolean());
        }

        Assert.Equal(ContextWindowDefault.FromPercent(50), store.Load(secrets, credentials).DefaultContextWindow);

        // Clearing removes only that property: the section, the address and the fields this build does not know stay.
        store.Save(definitions, "only", ContextWindowDefault.Empty, catalog.ModelCatalogSourceUrl);
        using var cleared = JsonDocument.Parse(File.ReadAllText(path));
        var clearedSection = cleared.RootElement.GetProperty("modelCatalog");
        Assert.False(clearedSection.TryGetProperty("defaultContextWindow", out _));
        Assert.True(clearedSection.TryGetProperty("generatedAt", out _));
        Assert.True(clearedSection.TryGetProperty("somethingThisBuildDoesNotKnow", out _));
    }

    [Fact]
    public void A_legacy_literal_key_moves_into_the_secret_store_and_the_definition_points_at_it()
    {
        var path = WriteSettings(
            """
            {
              "provider": {
                "kind": "openai-compatible",
                "baseUrl": "https://api.deepseek.com/v1",
                "model": "deepseek-chat",
                "apiKey": "sk-legacy-literal-key"
              }
            }
            """);
        var (store, secrets, credentials) = CreateStack(path);

        var catalog = store.Load(secrets, credentials);

        var provider = Assert.Single(catalog.Providers);
        // A legacy OpenAI-compatible entry becomes the custom preset, so its key reference follows that id.
        Assert.Equal("custom-openai-key", provider.Definition.SecretReference);
        Assert.Equal("sk-legacy-literal-key", secrets.Get("custom-openai-key"));
        Assert.Equal(ProviderStatusKind.Ready, provider.Status.Kind);

        // The first save drops the legacy section; the key must already be in the encrypted store by then.
        store.Save([provider.Definition], provider.Id, defaultContextWindow: null);
        Assert.Equal("sk-legacy-literal-key", secrets.Get("custom-openai-key"));
        Assert.DoesNotContain("sk-legacy-literal-key", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void A_settings_file_whose_root_is_not_an_object_is_refused_instead_of_replaced()
    {
        const string arrayRoot = "[1, 2, 3]";
        var path = WriteSettings(arrayRoot);
        var (store, _, _) = CreateStack(path);

        Assert.Throws<InvalidOperationException>(() => store.Save([], defaultId: null, defaultContextWindow: null));
        Assert.Equal(arrayRoot, File.ReadAllText(path));
    }

    [Fact]
    public void Nothing_configured_loads_no_provider_and_writes_nothing()
    {
        // No placeholder provider: an unsaved "fake" row would read as a connection the user never made.
        var path = Path.Combine(root, "settings.json");
        var (store, secrets, credentials) = CreateStack(path);

        var catalog = store.Load(secrets, credentials);

        Assert.Empty(catalog.Providers);
        Assert.Null(catalog.Default);
        Assert.False(catalog.IsConfigured);
        Assert.False(File.Exists(path), "nothing is written until the user saves a provider");
    }

    [Fact]
    public void A_providers_section_with_an_item_per_provider_loads_in_order_with_its_default()
    {
        var path = WriteSettings(
            """
            {
              "providers": {
                "version": 1,
                "defaultId": "second",
                "items": [
                  { "id": "first", "preset": "openai", "baseUrl": "https://api.openai.com/v1", "auth": { "kind": "api-key" }, "models": ["gpt"], "defaultModel": "gpt" },
                  { "id": "second", "preset": "deepseek", "baseUrl": "https://api.deepseek.com/v1", "auth": { "kind": "api-key" }, "models": ["deepseek-chat"], "defaultModel": "deepseek-chat" }
                ]
              }
            }
            """);
        var (store, secrets, credentials) = CreateStack(path);

        var catalog = store.Load(secrets, credentials);

        Assert.Equal(["first", "second"], catalog.Providers.Select(provider => provider.Id));
        Assert.Equal("second", catalog.Default?.Id);
    }

    public void Dispose()
    {
        TestTempFiles.DeleteQuietly(root);
    }

    private (ProviderStore Store, ProviderSecretStore Secrets, ProviderCredentials Credentials) CreateStack(string settingsPath)
    {
        var secrets = new ProviderSecretStore(root);
        var credentials = new ProviderCredentials(
            new HttpClient(),
            secrets,
            claudeCredentialsPath: Path.Combine(root, "no-claude-login.json"));
        return (new ProviderStore(settingsPath), secrets, credentials);
    }

    private string WriteSettings(string json)
    {
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }
}
