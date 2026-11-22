using Forge.Abstractions;
using Forge.App.Providers;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// What the model reads before choosing a spawn model: the connected models, each with the exact id to pass and the
/// steps it accepts. The description never carries the list, and the list is re-read from the live catalogue on every
/// call, so a catalogue refresh cannot move the cached prompt prefix.
/// </summary>
public sealed class ModelsToolTests : IDisposable
{
    private readonly ProviderCatalogFixture fixture = new();

    public void Dispose() => fixture.Dispose();

    private static ToolContext Context => new("session-models", ".");

    private static Task<ToolResult> RunAsync(ModelsTool tool, string? provider = null)
    {
        var arguments = provider is null ? "{}" : "{\"provider\":\"" + provider + "\"}";
        return tool.InvokeAsync(new ToolCall("c1", ModelsTool.Name, arguments), Context, CancellationToken.None).AsTask();
    }

    [Fact]
    public async Task Every_provider_lists_its_models_with_the_steps_each_one_accepts()
    {
        var catalog = fixture.Create(
            "opencode",
            new FixtureProvider("opencode", "OPENCODE", ["kimi-k2", "deepseek-v4.1-flash"]),
            new FixtureProvider("deepseek", "DEEPSEEK", ["deepseek-flash"]));

        var result = await RunAsync(new ModelsTool(() => catalog, null));

        Assert.False(result.IsError);
        Assert.Equal(
            "models: 2 providers, 3 models\n" +
            "\n" +
            "opencode (OPENCODE)\n" +
            "  " + "kimi-k2".PadRight(19) + "  thinking: off\n" +
            "  " + "deepseek-v4.1-flash".PadRight(19) + "  thinking: off, low, high, max\n" +
            "\n" +
            "deepseek (DEEPSEEK)\n" +
            "  " + "deepseek-flash".PadRight(14) + "  thinking: off, low, high, max",
            result.Text);
    }

    [Fact]
    public async Task A_hidden_model_is_not_listed_and_an_empty_provider_says_so()
    {
        var catalog = fixture.Create(
            "opencode",
            new FixtureProvider("opencode", "OPENCODE", ["kimi-k2", "deepseek-v4.1-flash"]),
            new FixtureProvider("deepseek", "DEEPSEEK", ["deepseek-flash"]));

        var result = await RunAsync(new ModelsTool(() => catalog, model => model is "kimi-k2" or "deepseek-v4.1-flash"));

        Assert.Equal(
            "models: 2 providers, 1 model\n" +
            "\n" +
            "opencode (OPENCODE)\n" +
            "  (no visible models)\n" +
            "\n" +
            "deepseek (DEEPSEEK)\n" +
            "  " + "deepseek-flash".PadRight(14) + "  thinking: off, low, high, max",
            result.Text);
    }

    [Fact]
    public async Task The_provider_argument_narrows_the_list_to_one_provider()
    {
        var catalog = fixture.Create(
            "opencode",
            new FixtureProvider("opencode", "OPENCODE", ["kimi-k2"]),
            new FixtureProvider("deepseek", "DEEPSEEK", ["deepseek-flash"]));

        var result = await RunAsync(new ModelsTool(() => catalog, null), "deepseek");

        Assert.Equal(
            "models: 1 provider, 1 model\n" +
            "\n" +
            "deepseek (DEEPSEEK)\n" +
            "  " + "deepseek-flash".PadRight(14) + "  thinking: off, low, high, max",
            result.Text);
    }

    [Fact]
    public async Task An_unknown_provider_is_an_error_that_lists_the_connected_ones()
    {
        var catalog = fixture.Create("probe", new FixtureProvider("probe", "Probe", ["kimi-k2"]));

        var result = await RunAsync(new ModelsTool(() => catalog, null), "nope");

        Assert.True(result.IsError);
        Assert.Contains("unknown provider 'nope'", result.Text);
        Assert.Contains("probe (Probe)", result.Text);
    }

    [Fact]
    public async Task No_connected_provider_is_an_error()
    {
        var catalog = fixture.Create(null);

        var result = await RunAsync(new ModelsTool(() => catalog, null));

        Assert.True(result.IsError);
        Assert.Contains("no provider is connected", result.Text);
    }

    [Fact]
    public async Task The_list_is_capped_and_says_how_many_models_it_left_out()
    {
        var models = Enumerable.Range(0, 401).Select(index => "model-" + index).ToArray();
        var catalog = fixture.Create("probe", new FixtureProvider("probe", "Probe", models));

        var result = await RunAsync(new ModelsTool(() => catalog, null));

        Assert.Contains("models: 1 provider, 401 models\n", result.Text);
        Assert.Contains("model-399", result.Text);
        Assert.DoesNotContain("model-400", result.Text);
        Assert.EndsWith(
            "... 1 more model not shown; call models with provider=<id> to narrow the list.",
            result.Text);
    }
}
