using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.IO.Tools;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The spec ids the evidence collection reads out of a <c>read</c> result, driven against the answer the production
/// <see cref="ReadTool"/> actually produces: a header line, then every line prefixed with its right-aligned number and
/// the arrow. The old scan looked for a raw frontmatter file head and never matched a single production result - these
/// tests are the proof it now reads the shape that really reaches the loop, and still pins nothing when the result is a
/// window, a gap, or a file with no frontmatter at all.
/// </summary>
public sealed class TurnLedgerEvidenceTests : IDisposable
{
    private const string SpecId = "forge-evidence-test-spec";

    private readonly string workspace = Directory.CreateTempSubdirectory("forge-turn-ledger-evidence-").FullName;

    public void Dispose() => Directory.Delete(workspace, recursive: true);

    /// <summary>The real tool's answer for one file, the way the loop's tool batch would hand it over.</summary>
    private async Task<string> ReadResultAsync(string relative, int? offset = null)
    {
        var arguments = new Dictionary<string, object> { ["path"] = relative };
        if (offset is { } first)
        {
            arguments["offset"] = first;
        }

        var result = await new ReadTool().InvokeAsync(
            new ToolCall("call", "read", JsonSerializer.Serialize(arguments)),
            new ToolContext("session", workspace),
            TestContext.Current.CancellationToken);
        Assert.False(result.IsError, result.Content);
        return result.Content;
    }

    private static IReadOnlyList<string> SpecsOf(string result)
    {
        var evidence = new TurnLedgerEvidence();
        evidence.Collect(
            [new ToolCallBlock("c1", "read", """{"path":"spec.md"}""")],
            [new ToolResultBlock("c1", result, IsError: false)]);
        return evidence.Specs;
    }

    [Fact]
    public async Task A_read_result_from_the_real_tool_yields_the_spec_id_its_frontmatter_carries()
    {
        File.WriteAllText(
            Path.Combine(workspace, "spec.md"),
            $"""
            ---
            id: {SpecId}
            type: module-design
            title: The spec under test
            ---

            body text
            """);

        // The result the real tool produced: a header line, then every line prefixed with its number and the arrow.
        var result = await ReadResultAsync("spec.md");
        Assert.StartsWith("spec.md: 7 lines", result, StringComparison.Ordinal);
        Assert.DoesNotContain($"{Environment.NewLine}---{Environment.NewLine}", result, StringComparison.Ordinal);

        Assert.Equal([SpecId], SpecsOf(result));
    }

    [Fact]
    public async Task A_wider_file_still_yields_its_id_with_the_numbers_right_aligned()
    {
        // Ten-plus lines, so the tool right-aligns the numbers and pads the first body line with a space - the prefix
        // strip has to see through the padding, not just a bare digit.
        var frontmatter = $"---\nid: {SpecId}\n---\n\nbody\n";
        File.WriteAllText(
            Path.Combine(workspace, "spec.md"),
            frontmatter + string.Join("\n", Enumerable.Range(0, 10).Select(static index => $"line {index}")));

        var result = await ReadResultAsync("spec.md");

        Assert.StartsWith("spec.md: 15 lines", result, StringComparison.Ordinal);
        Assert.Contains("\n 1→---", result, StringComparison.Ordinal);
        Assert.Equal([SpecId], SpecsOf(result));
    }

    [Fact]
    public async Task A_window_that_starts_past_the_frontmatter_pins_nothing()
    {
        File.WriteAllText(Path.Combine(workspace, "spec.md"), $"---\nid: {SpecId}\n---\n\nbody\n");

        var result = await ReadResultAsync("spec.md", offset: 2);

        Assert.Equal([], SpecsOf(result));
    }

    [Fact]
    public async Task A_file_without_frontmatter_pins_nothing()
    {
        File.WriteAllText(Path.Combine(workspace, "plain.md"), "just prose\nnothing to pin\n");

        var result = await ReadResultAsync("plain.md");

        Assert.Equal([], SpecsOf(result));
    }
}
