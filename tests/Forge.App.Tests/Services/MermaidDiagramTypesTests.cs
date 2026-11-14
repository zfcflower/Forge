using System.Numerics;
using Forge.App.Preferences;
using Forge.App.Services;
using MermaidKinds = Mermaider.Models.DiagramTypes;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Pins the one table behind the Mermaid whitelist (wave W11): the <c>chat</c> setting's tokens and the diagram headers of the
/// source are two directions of the same list, so a token can never enable one type in the settings and sniff another. A
/// diagram whose header is unknown is null (the renderer gets the last word and reports the failure), never a silent code block.
/// </summary>
public sealed class MermaidDiagramTypesTests
{
    /// <summary>The five types the shipped whitelist enables.</summary>
    private const MermaidKinds Default =
        MermaidKinds.Flowchart | MermaidKinds.Sequence | MermaidKinds.State | MermaidKinds.Class | MermaidKinds.Er;

    [Fact]
    public void The_shipped_whitelist_parses_to_the_five_default_types()
    {
        Assert.Equal(Default, MermaidDiagramTypes.Parse(AppPreferences.DefaultChatMermaidDiagramTypes));
        Assert.Equal(Default, MermaidDiagramTypes.Parse("flowchart,sequence,state,class,er"));
    }

    [Theory]
    [InlineData("  FLOWCHART , Sequence  ", MermaidKinds.Flowchart | MermaidKinds.Sequence)]
    [InlineData("pie,chart-typo", MermaidKinds.Pie)]
    [InlineData("flowchart,FLOWCHART,FlowChart", MermaidKinds.Flowchart)]
    [InlineData("flowchart,,,sequence", MermaidKinds.Flowchart | MermaidKinds.Sequence)]
    public void Tokens_are_case_insensitive_and_unknown_ones_are_ignored(string setting, MermaidKinds expected) =>
        Assert.Equal(expected, MermaidDiagramTypes.Parse(setting));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(", ,")]
    [InlineData("graph")]
    public void An_empty_whitelist_or_a_header_keyword_enables_nothing(string? setting) =>
        // "graph" is a header keyword, not a token: the setting only knows the tokens, so it must not widen the whitelist.
        Assert.Equal((MermaidKinds)0, MermaidDiagramTypes.Parse(setting));

    [Theory]
    [InlineData("flowchart TD\n  A-->B", MermaidKinds.Flowchart)]
    [InlineData("graph LR\n  A-->B", MermaidKinds.Flowchart)]
    [InlineData("sequenceDiagram\n  A->>B: hi", MermaidKinds.Sequence)]
    [InlineData("stateDiagram\n  A --> B", MermaidKinds.State)]
    [InlineData("stateDiagram-v2\n  A --> B", MermaidKinds.State)]
    [InlineData("classDiagram\n  class A", MermaidKinds.Class)]
    [InlineData("erDiagram\n  A ||--o{ B : has", MermaidKinds.Er)]
    [InlineData("gantt\n  title Plan", MermaidKinds.Gantt)]
    [InlineData("pie\n  \"a\" : 1", MermaidKinds.Pie)]
    [InlineData("journey\n  title Day", MermaidKinds.Journey)]
    [InlineData("gitGraph\n  commit", MermaidKinds.GitGraph)]
    [InlineData("mindmap\n  root", MermaidKinds.Mindmap)]
    [InlineData("timeline\n  title Time", MermaidKinds.Timeline)]
    [InlineData("quadrantChart\n  title Q", MermaidKinds.Quadrant)]
    [InlineData("requirementDiagram\n  requirement r", MermaidKinds.Requirement)]
    [InlineData("requirement\n  requirement r", MermaidKinds.Requirement)]
    [InlineData("sankey\n  A,B,1", MermaidKinds.Sankey)]
    [InlineData("xychart\n  title T", MermaidKinds.XyChart)]
    [InlineData("packet\n  0-7: \"x\"", MermaidKinds.Packet)]
    [InlineData("block\n  columns 1", MermaidKinds.Block)]
    [InlineData("architecture\n  group g", MermaidKinds.Architecture)]
    [InlineData("treeView\n  root", MermaidKinds.TreeView)]
    [InlineData("block-beta\n  columns 1", MermaidKinds.Block)]
    [InlineData("C4Context\n  title C", MermaidKinds.C4)]
    [InlineData("C4Container", MermaidKinds.C4)]
    [InlineData("C4Component", MermaidKinds.C4)]
    [InlineData("C4Dynamic", MermaidKinds.C4)]
    [InlineData("C4Deployment", MermaidKinds.C4)]
    [InlineData("sankey-beta\n  a,b,1", MermaidKinds.Sankey)]
    [InlineData("xychart-beta\n  bar [1]", MermaidKinds.XyChart)]
    [InlineData("block-beta\n  a", MermaidKinds.Block)]
    [InlineData("packet-beta\n  0-15: \"a\"", MermaidKinds.Packet)]
    [InlineData("kanban\n  todo", MermaidKinds.Kanban)]
    [InlineData("architecture-beta\n  group a(cloud)", MermaidKinds.Architecture)]
    [InlineData("radar-beta\n  axis a", MermaidKinds.Radar)]
    [InlineData("treemap-beta\n  \"a\": 1", MermaidKinds.Treemap)]
    [InlineData("venn-beta\n  sets a", MermaidKinds.Venn)]
    [InlineData("treeView-beta\n  a", MermaidKinds.TreeView)]
    [InlineData("FlowChart TD", MermaidKinds.Flowchart)]
    [InlineData("PIE", MermaidKinds.Pie)]
    public void Every_header_the_renderer_knows_is_detected(string source, MermaidKinds expected) =>
        Assert.Equal(expected, MermaidDiagramTypes.Detect(source));

    [Theory]
    [InlineData("---\ntitle: Demo\n---\nflowchart TD\n  A-->B", MermaidKinds.Flowchart)]
    [InlineData("---\r\ntitle: Demo\r\n---\r\nflowchart TD\r\n  A-->B", MermaidKinds.Flowchart)]
    [InlineData("\n\n  \nflowchart TD\n  A-->B", MermaidKinds.Flowchart)]
    [InlineData("%%{init: {\"theme\": \"dark\"}}%%\nflowchart TD\n  A-->B", MermaidKinds.Flowchart)]
    [InlineData("%% a comment\n%% another one\nsequenceDiagram\n  A->>B: hi", MermaidKinds.Sequence)]
    [InlineData("\uFEFFflowchart TD", MermaidKinds.Flowchart)]
    public void Front_matter_comments_and_blank_lines_before_the_header_are_skipped(string source, MermaidKinds expected) =>
        Assert.Equal(expected, MermaidDiagramTypes.Detect(source));

    [Theory]
    [InlineData("this is not mermaid at all")]
    [InlineData("A-->B\nflowchart TD")]
    [InlineData("graphite LR\n  A-->B")]
    [InlineData("piece of pie")]
    [InlineData("%% only a comment")]
    [InlineData("---\ntitle: unterminated\nflowchart TD")]
    [InlineData("")]
    public void An_unknown_header_is_null_so_the_renderer_reports_the_failure(string source) =>
        Assert.Null(MermaidDiagramTypes.Detect(source));

    [Fact]
    public void A_null_source_has_no_type() => Assert.Null(MermaidDiagramTypes.Detect(null));

    [Theory]
    [InlineData("flowchart", MermaidKinds.Flowchart)]
    [InlineData("sequence", MermaidKinds.Sequence)]
    [InlineData("state", MermaidKinds.State)]
    [InlineData("class", MermaidKinds.Class)]
    [InlineData("er", MermaidKinds.Er)]
    [InlineData("c4", MermaidKinds.C4)]
    public void A_token_names_one_kind_and_parses_back_to_it(string token, MermaidKinds kind)
    {
        Assert.Equal(token, MermaidDiagramTypes.Token(kind));
        Assert.Equal(kind, MermaidDiagramTypes.Parse(token));
    }

    [Fact]
    public void A_combination_of_kinds_has_no_single_token() =>
        Assert.Null(MermaidDiagramTypes.Token(MermaidKinds.Flowchart | MermaidKinds.Pie));

    [Fact]
    public void The_token_list_is_unique_and_covers_the_default_whitelist()
    {
        var tokens = MermaidDiagramTypes.Tokens;

        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(tokens, static token => Assert.False(string.IsNullOrWhiteSpace(token)));
        foreach (var expected in new[] { "flowchart", "sequence", "state", "class", "er" })
        {
            Assert.Contains(expected, tokens, StringComparer.OrdinalIgnoreCase);
        }

        // Each token enables exactly one type, and the list as a whole enables exactly what the tokens add up to.
        MermaidKinds union = 0;
        foreach (var token in tokens)
        {
            var kinds = MermaidDiagramTypes.Parse(token);
            Assert.Equal(1, BitOperations.PopCount((uint)kinds));
            union |= kinds;
        }

        Assert.Equal(union, MermaidDiagramTypes.Parse(string.Join(",", tokens)));
    }
}
