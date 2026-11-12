using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// <see cref="ComposerPreferences"/> stores the composer's effort as one of the seven lowercase names. Unknown names keep
/// the old fallback (no default), and every step survives the trip through <c>composer.json</c>.
/// </summary>
public sealed class ComposerPreferencesTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-composer-preferences-");


    [Theory]
    [InlineData("off", ThinkingEffort.Off)]
    [InlineData("minimal", ThinkingEffort.Minimal)]
    [InlineData("low", ThinkingEffort.Low)]
    [InlineData("medium", ThinkingEffort.Medium)]
    [InlineData("high", ThinkingEffort.High)]
    [InlineData("xhigh", ThinkingEffort.XHigh)]
    [InlineData("max", ThinkingEffort.Max)]
    public void Every_effort_name_round_trips(string name, ThinkingEffort effort)
    {
        Assert.Equal(name, ComposerPreferences.EffortName(effort));
        Assert.Equal(effort, ComposerPreferences.ParseEffort(name));
        Assert.Equal(effort, ComposerPreferences.ParseEffort(name.ToUpperInvariant()));
    }

    [Fact]
    public void An_unknown_or_missing_effort_name_has_no_default()
    {
        Assert.Null(ComposerPreferences.ParseEffort("ultra"));
        Assert.Null(ComposerPreferences.ParseEffort(null));
        Assert.Null(ComposerPreferences.ParseEffort(string.Empty));
    }

    [Fact]
    public void A_stored_xhigh_default_loads()
    {
        var path = Path.Combine(root, "composer.json");
        File.WriteAllText(path, """{ "defaultEffort": "xhigh" }""");

        var preferences = new ComposerPreferences(path);

        Assert.Equal(ThinkingEffort.XHigh, preferences.DefaultEffort);
    }

    [Fact]
    public void Saving_the_max_default_writes_the_lowercase_name()
    {
        var path = Path.Combine(root, "composer.json");
        var preferences = new ComposerPreferences(path);

        preferences.SetDefaults(agent: null, model: "m", ThinkingEffort.Max);

        var watch = Stopwatch.StartNew();
        string text = string.Empty;
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (File.Exists(path))
            {
                text = File.ReadAllText(path);
                if (text.Contains("\"defaultEffort\": \"max\"", StringComparison.Ordinal))
                {
                    break;
                }
            }

            Thread.Sleep(10);
        }

        Assert.Contains("\"defaultEffort\": \"max\"", text, StringComparison.Ordinal);
        Assert.Equal(ThinkingEffort.Max, new ComposerPreferences(path).DefaultEffort);
    }

    public void Dispose()
    {
        TestTempFiles.DeleteQuietly(root);
    }
}
