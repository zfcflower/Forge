using System.Text.Json;
using Forge.App.Providers;
using Forge.Core.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// Pins the <c>claudeWire</c> section: the only switch is <c>mode</c>, an invalid value falls back to its documented default
/// instead of disabling the whole section, and a missing section means the request stays exactly what it always was.
/// </summary>
public sealed class ClaudeWireSettingsTests
{
    [Fact]
    public void The_switch_is_one_boolean_and_only_false_turns_the_claim_off()
    {
        Assert.Null(ClaudeWireSettings.Resolve(Switch("false")));
        Assert.NotNull(ClaudeWireSettings.Resolve(Switch("true")));
    }

    [Fact]
    public void A_switch_that_is_not_a_boolean_reads_as_the_default()
    {
        // One spelling is the rule. An unreadable value means "nothing was said", so the documented default (on) applies rather
        // than the opposite of whatever the typo happened to look like.
        Assert.NotNull(ClaudeWireSettings.Resolve(Switch("\"off\"")));
        Assert.NotNull(ClaudeWireSettings.Resolve(Switch("\"false\"")));
        Assert.NotNull(ClaudeWireSettings.Resolve(Switch("0")));
        Assert.NotNull(ClaudeWireSettings.Resolve(Switch("{}")));
    }

    [Fact]
    public void A_missing_section_claims_the_official_client_with_the_documented_defaults()
    {
        // The claim is the product's default; the section exists to inspect it and to switch it off.
        var wire = ClaudeWireSettings.Resolve(null);

        Assert.NotNull(wire);
        Assert.Equal(ClaudeWireSettings.DefaultVersion, wire.Version);
        Assert.Equal(ClaudeWireSettings.DefaultEntrypoint, wire.Entrypoint);
    }

    [Fact]
    public void Official_fills_every_identity_value_from_the_documented_defaults()
    {
        var wire = ClaudeWireSettings.Resolve(Switch("true"));

        Assert.NotNull(wire);
        Assert.Equal(ClaudeWireSettings.DefaultVersion, wire.Version);
        // No build is pinned by default: the real one is a value the local CLI produces at runtime, so the section asks for it
        // instead of freezing a constant that would be wrong on most machines.
        Assert.Equal(string.Empty, wire.Build);
        // The default is the label that measured cheapest on the five-hour window, not the most plausible-sounding one.
        Assert.Equal("sdk-cli", wire.Entrypoint);
        Assert.Equal(ClaudeWireSettings.DefaultEntrypoint, wire.Entrypoint);
        Assert.Equal(AnthropicWire.Betas, wire.Betas);
    }

    [Fact]
    public void Values_are_taken_as_written_when_they_fit_their_shape()
    {
        var wire = ClaudeWireSettings.Resolve(new ClaudeWireSectionDocument
        {
            Enabled = JsonDocument.Parse("true").RootElement.Clone(),
            Version = "2.1.323",
            Build = "abc",
            Entrypoint = "claude-vscode",
        });

        Assert.NotNull(wire);
        Assert.Equal("2.1.323", wire.Version);
        Assert.Equal("abc", wire.Build);
        Assert.Equal("claude-vscode", wire.Entrypoint);
        Assert.Equal("claude-cli/2.1.323 (external, claude-vscode)", AnthropicWire.UserAgent(wire));
    }

    [Theory]
    [InlineData("", "", "")]                       // empty
    [InlineData("2.1", "68A", "Claude-Vscode")]    // wrong shapes: two parts, uppercase, uppercase first letter
    [InlineData("two.one", "with space", "9lives")]// non-numeric version, space, digit first
    public void Wrong_shapes_fall_back_to_the_default_instead_of_disabling_the_claim(string version, string build, string entrypoint)
    {
        var wire = ClaudeWireSettings.Resolve(new ClaudeWireSectionDocument
        {
            Enabled = JsonDocument.Parse("true").RootElement.Clone(),
            Version = version,
            Build = build,
            Entrypoint = entrypoint,
        });

        Assert.NotNull(wire);
        Assert.Equal(ClaudeWireSettings.DefaultVersion, wire.Version);
        Assert.Equal(string.Empty, wire.Build);
        Assert.Equal(ClaudeWireSettings.DefaultEntrypoint, wire.Entrypoint);
    }

    [Fact]
    public void The_documentation_sentence_names_every_value_it_accepts()
    {
        foreach (var token in new[] { "enabled", "true", "false", "version", "build", "entrypoint", "note" })
        {
            Assert.Contains(token, ClaudeWireSettings.Documentation, StringComparison.Ordinal);
        }
    }

    /// <summary>A section whose switch is written exactly as the given JSON token.</summary>
    private static ClaudeWireSectionDocument Switch(string json) => new()
    {
        Enabled = JsonDocument.Parse(json).RootElement.Clone(),
    };
}
