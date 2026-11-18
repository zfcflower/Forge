using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>
/// The spec-graph tool surface: get, graph and validate, all read-only (writing a spec is a file edit, and searching
/// one is the ordinary grep). Each call resolves a <see cref="SpecGraph"/> for the tool context's working directory, refreshing
/// the index first so the model never reads a stale tree.
/// </summary>
public sealed class SpecTools
{
    private readonly Func<string, SpecGraph> resolve;

    public SpecTools()
        : this(CreateCachingFactory())
    {
    }

    public SpecTools(SpecGraph graph)
        : this(FixedFactory(graph))
    {
    }

    public SpecTools(Func<string, SpecGraph> graphFactory)
    {
        ArgumentNullException.ThrowIfNull(graphFactory);
        resolve = graphFactory;

        Get = new SpecGetTool(graphFactory);
        Graph = new SpecGraphTool(graphFactory);
        Validate = new SpecValidateTool(graphFactory);

        All = [Get, Graph, Validate];
    }

    public ITool Get { get; }

    public ITool Graph { get; }

    public ITool Validate { get; }

    public IReadOnlyList<ITool> All { get; }

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static Func<string, SpecGraph> FixedFactory(SpecGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return _ => graph;
    }

    private static Func<string, SpecGraph> CreateCachingFactory()
    {
        var graphs = new ConcurrentDictionary<string, SpecGraph>(PathComparer);
        return workingDirectory => graphs.GetOrAdd(
            WorkspacePaths.Normalize(string.IsNullOrWhiteSpace(workingDirectory) ? Directory.GetCurrentDirectory() : workingDirectory),
            static directory => new SpecGraph(directory));
    }

    private abstract class SpecToolBase(Func<string, SpecGraph> resolve, string name, string description, string schema, ToolSideEffect sideEffect) : ITool
    {
        public ToolDefinition Definition { get; } = new(name, description, schema);

        public ToolSideEffect SideEffect { get; } = sideEffect;

        public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(call);
            ArgumentNullException.ThrowIfNull(context);

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            }
            catch (JsonException exception)
            {
                return new ToolResult($"{Definition.Name} arguments are not valid JSON: {exception.Message}", IsError: true);
            }

            using (document)
            {
                try
                {
                    return await InvokeCoreAsync(document.RootElement, resolve(context.WorkingDirectory), ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
                {
                    return new ToolResult($"{Definition.Name} failed: {exception.Message}", IsError: true);
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    return new ToolResult($"{Definition.Name}: the regex did not finish within the match timeout.", IsError: true);
                }
            }
        }

        protected abstract ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, SpecGraph graph, CancellationToken ct);

        /// <summary>Re-indexes before a query; an explicit <c>refresh</c> argument forces a content re-read.</summary>
        protected static ValueTask RefreshAsync(JsonElement arguments, SpecGraph graph, CancellationToken ct) =>
            new(graph.RefreshAsync(SpecToolJson.Boolean(arguments, "refresh", false), ct));

        protected ToolResult Success(object payload) => new(SpecToolJson.Serialize(payload));

        protected static string? RequiredString(JsonElement root, string name) => SpecToolJson.RequiredString(root, name);
    }

    private sealed class SpecGetTool(Func<string, SpecGraph> resolve) : SpecToolBase(
        resolve,
        "spec_get",
        "Read one spec by id with its frontmatter, resolved forward links and the specs that link back to it.",
        """{"type":"object","properties":{"id":{"type":"string","description":"Spec id from the frontmatter."},"refresh":{"type":"boolean","description":"Re-read every spec file instead of trusting size and write time; default false."}},"required":["id"],"additionalProperties":false}""",
        ToolSideEffect.ReadOnly)
    {
        protected override async ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, SpecGraph graph, CancellationToken ct)
        {
            var id = RequiredString(arguments, "id")!;
            await RefreshAsync(arguments, graph, ct).ConfigureAwait(false);
            var view = await graph.GetAsync(id, ct).ConfigureAwait(false);
            if (view is null)
            {
                return new ToolResult($"No spec with id '{id}' exists in '{graph.WorkspaceRoot}'.", IsError: true);
            }

            return Success(SpecToolJson.Describe(view));
        }
    }

    private sealed class SpecGraphTool(Func<string, SpecGraph> resolve) : SpecToolBase(
        resolve,
        "spec_graph",
        "Walk the spec tree from one id: descendants, ancestors, or the links of one edge kind in both directions.",
        """
        {"type":"object","properties":{"id":{"type":"string","description":"Spec id to start from."},"direction":{"type":"string","enum":["subtree","ancestors","neighbors"],"description":"Traversal direction; default subtree."},"edge":{"type":"string","enum":["parent","depends-on","references","implements","covers"],"description":"Edge kind for neighbors; default depends-on."},"depth":{"type":"integer","description":"Hops to expand; default 1, at most 10."},"refresh":{"type":"boolean","description":"Re-read every spec file instead of trusting size and write time; default false."}},"required":["id"],"additionalProperties":false}
        """,
        ToolSideEffect.ReadOnly)
    {
        protected override async ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, SpecGraph graph, CancellationToken ct)
        {
            var id = RequiredString(arguments, "id")!;
            var direction = SpecToolJson.String(arguments, "direction") switch
            {
                null or "subtree" => SpecGraphDirection.Subtree,
                "ancestors" => SpecGraphDirection.Ancestors,
                "neighbors" => SpecGraphDirection.Neighbors,
                var other => throw new ArgumentException($"Unknown direction '{other}'; expected subtree, ancestors or neighbors."),
            };
            var edge = SpecToolJson.ParseEdge(SpecToolJson.String(arguments, "edge")) ?? SpecEdgeKind.DependsOn;
            var depth = SpecToolJson.Integer(arguments, "depth", 1);

            await RefreshAsync(arguments, graph, ct).ConfigureAwait(false);
            var slice = await graph.GetGraphAsync(id, direction, depth, edge, ct).ConfigureAwait(false);
            if (slice is null)
            {
                return new ToolResult($"No spec with id '{id}' exists in '{graph.WorkspaceRoot}'.", IsError: true);
            }

            return Success(new Dictionary<string, object?>
            {
                ["root"] = slice.RootId,
                ["direction"] = slice.Direction.ToString().ToLowerInvariant(),
                ["depth"] = slice.Depth,
                ["nodes"] = slice.Nodes
                    .Select(static node => (object?)new Dictionary<string, object?>
                    {
                        ["id"] = node.Node.Id,
                        ["type"] = node.Node.Type,
                        ["title"] = node.Node.Title,
                        ["status"] = node.Node.Status,
                        ["path"] = node.Node.Path,
                        ["depth"] = node.Depth,
                    })
                    .ToArray(),
                ["edges"] = slice.Edges
                    .Select(static edgeLink => (object?)new Dictionary<string, object?>
                    {
                        ["from"] = edgeLink.FromId,
                        ["kind"] = SpecToolJson.EdgeName(edgeLink.Kind),
                        ["to"] = edgeLink.ToId,
                        ["reverse"] = edgeLink.IsReverse,
                    })
                    .ToArray(),
            });
        }
    }

    private sealed class SpecValidateTool(Func<string, SpecGraph> resolve) : SpecToolBase(
        resolve,
        "spec_validate",
        "Check the spec tree for duplicate ids, missing parents, dangling links and parent cycles.",
        """{"type":"object","properties":{"refresh":{"type":"boolean","description":"Re-read every spec file instead of trusting size and write time; default false."}},"additionalProperties":false}""",
        ToolSideEffect.ReadOnly)
    {
        protected override async ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, SpecGraph graph, CancellationToken ct)
        {
            await RefreshAsync(arguments, graph, ct).ConfigureAwait(false);
            var result = await graph.ValidateAsync(ct).ConfigureAwait(false);
            return Success(new Dictionary<string, object?>
            {
                ["valid"] = result.IsValid,
                ["issues"] = result.Issues
                    .Select(static issue => (object?)new Dictionary<string, object?>
                    {
                        ["code"] = SpecToolJson.IssueCode(issue.Code),
                        ["id"] = issue.SpecId,
                        ["path"] = issue.Path,
                        ["message"] = issue.Message,
                    })
                    .ToArray(),
            });
        }
    }

}

internal static class SpecToolJson
{
    public static string Serialize(object payload) => JsonPayload.Serialize(payload);

    public static string? String(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static string? RequiredString(JsonElement root, string name)
    {
        var value = String(root, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"'{name}' must be a non-empty string.");
        }

        return value;
    }

    public static bool Boolean(JsonElement root, string name, bool fallback) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    public static int Integer(JsonElement root, string name, int fallback) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : fallback;

    public static SpecEdgeKind? ParseEdge(string? name) => name switch
    {
        null => null,
        "parent" => SpecEdgeKind.Parent,
        "depends-on" or "depends_on" => SpecEdgeKind.DependsOn,
        "references" => SpecEdgeKind.References,
        "implements" => SpecEdgeKind.Implements,
        "covers" => SpecEdgeKind.Covers,
        _ => throw new ArgumentException($"Unknown edge '{name}'."),
    };

    public static string EdgeName(SpecEdgeKind kind) => kind switch
    {
        SpecEdgeKind.Parent => "parent",
        SpecEdgeKind.DependsOn => "depends-on",
        SpecEdgeKind.References => "references",
        SpecEdgeKind.Implements => "implements",
        _ => "covers",
    };

    public static string IssueCode(SpecIssueCode code) => code switch
    {
        SpecIssueCode.DuplicateId => "duplicate-id",
        SpecIssueCode.DanglingParent => "dangling-parent",
        SpecIssueCode.DanglingLink => "dangling-link",
        SpecIssueCode.ParentCycle => "parent-cycle",
        _ => "unknown",
    };

    public static object Describe(SpecNodeView view) => new Dictionary<string, object?>
    {
        ["id"] = view.Node.Id,
        ["type"] = view.Node.Type,
        ["title"] = view.Node.Title,
        ["status"] = view.Node.Status,
        ["path"] = view.Node.Path,
        ["frontmatter"] = view.Frontmatter.Fields.ToDictionary(
            static field => field.Name,
            static field => DescribeField(field),
            StringComparer.Ordinal),
        ["forward_links"] = view.ForwardLinks
            .Select(static link => (object?)new Dictionary<string, object?>
            {
                ["kind"] = EdgeName(link.Kind),
                ["target"] = link.TargetId,
                ["exists"] = link.Target is not null,
            })
            .ToArray(),
        ["reverse_links"] = view.ReverseLinks
            .Select(static link => (object?)new Dictionary<string, object?>
            {
                ["kind"] = EdgeName(link.Kind),
                ["source"] = link.TargetId,
                ["title"] = link.Target?.Title,
                ["path"] = link.Target?.Path,
            })
            .ToArray(),
    };

    private static object? DescribeField(SpecField field) => SpecFrontmatter.IsListField(field.Name)
        ? field.Values.ToArray()
        : field.RawText ?? (field.Values.Count == 1 ? field.Values[0] : (object)field.Values.ToArray());

    private static string NormalizeName(string name) => name.Replace('_', '-');
}
