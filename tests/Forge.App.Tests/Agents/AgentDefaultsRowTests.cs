using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Providers;
using Forge.App.Settings;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// One row of the settings page's built-in agent defaults. The model combo has to offer the stored value even when no
/// provider lists that model any more: an invisible stale value reads as the blank "not set" row, which would silently
/// change what the next session runs on.
/// </summary>
public sealed class AgentDefaultsRowTests : IDisposable
{
    private const string NotSet = "not set";

    private static readonly ThinkingEffort[] EveryStep =
    [
        ThinkingEffort.Off, ThinkingEffort.Minimal, ThinkingEffort.Low, ThinkingEffort.Medium, ThinkingEffort.High,
        ThinkingEffort.XHigh, ThinkingEffort.Max,
    ];

    private readonly string root = TestTempFiles.NewDirectory("forge-agent-defaults-row-");
    private readonly ProviderSecretStore secrets;
    private readonly ProviderCredentials credentials;

    public AgentDefaultsRowTests()
    {
        Directory.CreateDirectory(root);
        secrets = new ProviderSecretStore(root);
        credentials = new ProviderCredentials(
            new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            secrets,
            Path.Combine(root, "missing-credentials.json"));
    }

    [Fact]
    public void A_stored_model_no_provider_lists_is_appended_and_stays_selected()
    {
        var row = Row(new AgentDefault("alpha", "retired-model", ThinkingEffort.High));

        // Not set, the provider's listed model, then the stale record from settings.json.
        Assert.Equal(3, row.Models.Count);
        var stale = Assert.Single(row.Models, choice => choice.Model == "retired-model");
        Assert.Equal("alpha", stale.ProviderId);
        Assert.Equal("alpha · retired-model", stale.Display);
        Assert.Equal(stale, row.Models[^1]);
        // The combo shows the stored value and Current still round-trips it, so saving the row keeps the entry it had.
        Assert.Equal(stale, row.SelectedModel);
        Assert.NotNull(row.Current);
        Assert.Equal("retired-model", row.Current!.Model);
        Assert.Equal("alpha", row.Current.ProviderId);
        Assert.Equal(ThinkingEffort.High, row.Current.Thinking);
    }

    [Fact]
    public void A_stored_model_a_provider_lists_is_selected_without_a_duplicate_row()
    {
        var row = Row(
            new AgentDefault("alpha", "alpha-1", ThinkingEffort.Max),
            levels: (_, _) => [ThinkingEffort.Off, ThinkingEffort.Low]);

        Assert.Equal(2, row.Models.Count);
        Assert.Equal("alpha-1", row.SelectedModel.Model);
        Assert.Single(row.Models, choice => choice.Model == "alpha-1");
        // The step is still clamped to what the model accepts, stale entry or not.
        Assert.Equal(ThinkingEffort.Low, row.Current!.Thinking);
    }

    public void Dispose()
    {
        TestTempFiles.DeleteQuietly(root);
    }

    private AgentDefaultsRowViewModel Row(
        AgentDefault? stored,
        Func<string?, string?, IReadOnlyList<ThinkingEffort>>? levels = null) =>
        new(AgentIds.Main, "Main", stored, Catalog(), NotSet, levels ?? ((_, _) => EveryStep));

    private ProviderCatalog Catalog() => ProviderCatalog.Create([Definition()], "alpha", secrets, credentials);

    private static ProviderDefinition Definition() => new()
    {
        Id = "alpha",
        PresetId = ProviderPresets.CustomId,
        DisplayName = "alpha",
        Protocol = ProviderProtocol.OpenAiCompatible,
        BaseUrl = "https://alpha.invalid/v1",
        CredentialKind = ProviderCredentialKind.ApiKey,
        SecretReference = "provider:alpha",
        Models = ["alpha-1"],
        DefaultModel = "alpha-1",
        Overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal),
    };
}
