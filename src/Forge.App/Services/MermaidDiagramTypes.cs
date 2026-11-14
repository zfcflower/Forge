using Mermaider.Models;
using MermaidKinds = Mermaider.Models.DiagramTypes;

namespace Forge.App.Services;

/// <summary>
/// The Mermaid diagram types the chat and the diff preview may render (wave W11): the setting's comma-separated tokens and
/// the header line of a diagram source both map onto Mermaider's <see cref="DiagramTypes"/> flags. One table owns both
/// directions, so a token can never mean one type in the setting and another in the sniffing.
/// <para>Sniffing exists so a diagram whose type is not enabled is rendered as an ordinary code block without ever entering
/// the renderer: the whitelist is a display decision, not a failure.</para>
/// </summary>
internal static class MermaidDiagramTypes
{
    /// <summary>One Mermaid header keyword, the flag it selects, and the token the setting uses for it.</summary>
    private readonly record struct Header(string Keyword, MermaidKinds Kind, string Token);

    /// <summary>
    /// The header keywords Mermaider detects (its <c>DiagramDetector</c> table), longest keyword first - <c>stateDiagram-v2</c>
    /// must win over <c>stateDiagram</c>, and <c>flowchart</c> over <c>graph</c>. Order is part of the contract. Mermaider also
    /// takes the un-suffixed forms of the <c>-beta</c> types (<c>sankey</c>, <c>block</c>, ...), so both are listed.
    /// </summary>
    private static readonly Header[] Headers =
    [
        new("stateDiagram-v2", MermaidKinds.State, "state"),
        new("stateDiagram", MermaidKinds.State, "state"),
        new("sequenceDiagram", MermaidKinds.Sequence, "sequence"),
        new("classDiagram", MermaidKinds.Class, "class"),
        new("erDiagram", MermaidKinds.Er, "er"),
        new("requirementDiagram", MermaidKinds.Requirement, "requirement"),
        new("requirement", MermaidKinds.Requirement, "requirement"),
        new("quadrantChart", MermaidKinds.Quadrant, "quadrant"),
        new("architecture-beta", MermaidKinds.Architecture, "architecture"),
        new("architecture", MermaidKinds.Architecture, "architecture"),
        new("treeView-beta", MermaidKinds.TreeView, "treeview"),
        new("treeView", MermaidKinds.TreeView, "treeview"),
        new("treemap-beta", MermaidKinds.Treemap, "treemap"),
        new("xychart-beta", MermaidKinds.XyChart, "xychart"),
        new("xychart", MermaidKinds.XyChart, "xychart"),
        new("sankey-beta", MermaidKinds.Sankey, "sankey"),
        new("sankey", MermaidKinds.Sankey, "sankey"),
        new("packet-beta", MermaidKinds.Packet, "packet"),
        new("packet", MermaidKinds.Packet, "packet"),
        new("block-beta", MermaidKinds.Block, "block"),
        new("block", MermaidKinds.Block, "block"),
        new("radar-beta", MermaidKinds.Radar, "radar"),
        new("venn-beta", MermaidKinds.Venn, "venn"),
        new("C4Context", MermaidKinds.C4, "c4"),
        new("C4Container", MermaidKinds.C4, "c4"),
        new("C4Component", MermaidKinds.C4, "c4"),
        new("C4Dynamic", MermaidKinds.C4, "c4"),
        new("C4Deployment", MermaidKinds.C4, "c4"),
        new("flowchart", MermaidKinds.Flowchart, "flowchart"),
        new("graph", MermaidKinds.Flowchart, "flowchart"),
        new("gitGraph", MermaidKinds.GitGraph, "gitgraph"),
        new("mindmap", MermaidKinds.Mindmap, "mindmap"),
        new("timeline", MermaidKinds.Timeline, "timeline"),
        new("journey", MermaidKinds.Journey, "journey"),
        new("kanban", MermaidKinds.Kanban, "kanban"),
        new("gantt", MermaidKinds.Gantt, "gantt"),
        new("pie", MermaidKinds.Pie, "pie"),
    ];

    /// <summary>The tokens the setting accepts, in a stable order for the settings hint (the aliases appear once).</summary>
    internal static IReadOnlyList<string> Tokens { get; } = BuildTokens();

    /// <summary>
    /// The flags of <paramref name="setting"/> (the <c>chat</c> Mermaid whitelist). Tokens are case-insensitive and
    /// whitespace-separated by commas; unknown tokens are ignored, so a typo narrows the whitelist instead of throwing
    /// from a data template. An empty or whitespace-only setting enables nothing, which is the honest reading of it.
    /// </summary>
    internal static MermaidKinds Parse(string? setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
        {
            return 0;
        }

        var kinds = (MermaidKinds)0;
        foreach (var raw in setting.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var header in Headers)
            {
                if (string.Equals(header.Token, raw, StringComparison.OrdinalIgnoreCase))
                {
                    kinds |= header.Kind;
                    break;
                }
            }
        }

        return kinds;
    }

    /// <summary>
    /// The diagram type <paramref name="source"/> declares, or null when the header is not one Mermaider knows (the renderer
    /// then gets the last word, and a bad header surfaces as a render failure instead of a silent code block).
    /// <para>YAML front matter (<c>---</c> … <c>---</c>) and <c>%%</c> comments/directives before the header are skipped, the way
    /// Mermaid itself allows them.</para>
    /// </summary>
    internal static MermaidKinds? Detect(string? source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return null;
        }

        var lines = source.AsSpan();
        var frontMatterPossible = true;
        while (!lines.IsEmpty)
        {
            var end = lines.IndexOf('\n');
            var line = (end < 0 ? lines : lines[..end]).TrimEnd('\r').TrimStart('\uFEFF').Trim();
            lines = end < 0 ? default : lines[(end + 1)..];
            if (line.IsEmpty)
            {
                continue;
            }

            if (frontMatterPossible && line.SequenceEqual("---"))
            {
                // A title/metadata block: skip to its closing delimiter; without one the source is not a diagram.
                frontMatterPossible = false;
                if (!SkipFrontMatter(ref lines))
                {
                    return null;
                }

                continue;
            }

            frontMatterPossible = false;
            if (line.StartsWith("%%", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var header in Headers)
            {
                if (line.StartsWith(header.Keyword, StringComparison.OrdinalIgnoreCase) &&
                    (line.Length == header.Keyword.Length ||
                        char.IsWhiteSpace(line[header.Keyword.Length]) ||
                        line[header.Keyword.Length] is ':' or '['))
                {
                    return header.Kind;
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>The token the setting uses for <paramref name="kind"/> (for messages and accessible names); null for a combination.</summary>
    internal static string? Token(MermaidKinds kind)
    {
        foreach (var header in Headers)
        {
            if (header.Kind == kind)
            {
                return header.Token;
            }
        }

        return null;
    }

    private static bool SkipFrontMatter(ref ReadOnlySpan<char> lines)
    {
        while (!lines.IsEmpty)
        {
            var end = lines.IndexOf('\n');
            var line = (end < 0 ? lines : lines[..end]).TrimEnd('\r').Trim();
            lines = end < 0 ? default : lines[(end + 1)..];
            if (line.SequenceEqual("---"))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> BuildTokens()
    {
        var tokens = new List<string>();
        foreach (var header in Headers)
        {
            if (!tokens.Contains(header.Token, StringComparer.Ordinal))
            {
                tokens.Add(header.Token);
            }
        }

        tokens.Sort(StringComparer.Ordinal);
        return tokens;
    }
}
