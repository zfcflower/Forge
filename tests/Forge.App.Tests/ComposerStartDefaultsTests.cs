using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Core.Providers;
using Forge.Core.Prompts.Templates;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// Where a brand-new chat starts (forge-app "输入条"): the main agent, on the model and effort <c>settings.json</c>
/// configures for it - the one place a default lives. <c>composer.json</c> used to carry its own
/// <c>defaultAgent</c>/<c>defaultModel</c>/<c>defaultEffort</c>, which decided the session's identity before the first
/// keystroke; those fields are ignored now, and this test is what keeps them from coming back.
/// </summary>
public sealed class ComposerStartDefaultsTests : IDisposable
{
    private const string MainModel = "glm-5.3";
    private const string LegacyModel = "model-a";

    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-composer-start-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient http = new();
    private ComposerViewModel? composer;

    public void Dispose()
    {
        composer?.Dispose();
        http.Dispose();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A background model-catalog write may still hold a handle; the tree is only this test's temp folder.
        }
    }

    [Fact]
    public void A_new_chat_starts_on_the_main_agent_with_the_model_its_settings_entry_names_whatever_composer_json_still_holds()
    {
        Directory.CreateDirectory(directory);
        var preferencesPath = Path.Combine(directory, "composer.json");
        File.WriteAllText(
            preferencesPath,
            $$"""
            {
              "pinnedModels": ["{{LegacyModel}}"],
              "defaultAgent": "Worker",
              "defaultModel": "{{LegacyModel}}",
              "defaultEffort": "low"
            }
            """);
        var preferences = new ComposerPreferences(preferencesPath);
        var providers = Providers();

        composer = new ComposerViewModel(
            providers,
            _ => { },
            preferences,
            directory,
            fileSource: _ => Task.FromResult<IReadOnlyList<string>>([]),
            skillSource: () => [],
            templateSource: () => PromptTemplateSet.BuiltIn(),
            agentDefaults: id => id switch
            {
                AgentIds.Main => new ModelSettings(MainModel, ThinkingEffort.High, ProviderId: "probe"),
                AgentIds.Worker => new ModelSettings(LegacyModel, ThinkingEffort.Low, ProviderId: "probe"),
                _ => null,
            },
            thinkingLevels: static (_, _) => [ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.High]);

        Assert.Equal(MainModel, composer.Model);
        Assert.Equal("probe", composer.ProviderId);
        Assert.Equal(ThinkingEffort.High, composer.Effort);

        // Only the three default fields are gone: the pins in the same file still load.
        Assert.True(preferences.IsPinned(LegacyModel));
    }

    /// <summary>One connected provider that serves both models, so a provider mismatch cannot mask a model mismatch.</summary>
    private ProviderServices Providers()
    {
        var providers = new ProviderServices(Path.Combine(directory, "settings.json"), directory, http);
        providers.Store.Save(
            [
                new ProviderDefinition
                {
                    Id = "probe",
                    PresetId = "custom",
                    DisplayName = "Probe",
                    Protocol = ProviderProtocol.OpenAiCompatible,
                    BaseUrl = "https://example.invalid",
                    CredentialKind = ProviderCredentialKind.ApiKey,
                    Models = [LegacyModel, MainModel],
                    DefaultModel = LegacyModel,
                    Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
                },
            ],
            "probe",
            ContextWindowDefault.Empty,
            providers.Catalog.ModelCatalogSourceUrl);
        providers.Reload();
        Assert.True(providers.Catalog.IsConfigured);
        return providers;
    }
}
