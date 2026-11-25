using System.Text.Json;

namespace Forge.Abstractions;

public enum ToolSideEffect { ReadOnly, Write, Execute }

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>
/// What a tool returns: text and images in order. A text-only tool writes <c>new ToolResult("text")</c>; <see cref="Content"/> is
/// the flattened text for every consumer that reads text (an image shows as its placeholder, never as base64). Only
/// <see cref="TextBlock"/> and <see cref="ImageBlock"/> are allowed in <paramref name="Blocks"/>, and the list is treated as immutable once passed in.
/// </summary>
public sealed record ToolResult(IReadOnlyList<ContentBlock> Blocks, bool IsError = false)
{
    public ToolResult(string Content, bool IsError = false)
        : this([new TextBlock(Content)], IsError)
    {
    }

    private readonly IReadOnlyList<ContentBlock> blocks = ToolResultBlocks.Validate(Blocks);

    /// <summary>Text and image blocks only; a <c>with</c> expression is validated too.</summary>
    public IReadOnlyList<ContentBlock> Blocks
    {
        get => blocks;
        init => blocks = ToolResultBlocks.Validate(value);
    }

    /// <summary>The flattened text of <see cref="Blocks"/>; see <see cref="ToolResultBlocks.Flatten"/>.</summary>
    public string Content => ToolResultBlocks.Flatten(Blocks);

    /// <summary>The tool's own words, without a placeholder for its images; see <see cref="ToolResultBlocks.TextOnly"/>.</summary>
    public string Text => ToolResultBlocks.TextOnly(Blocks);

    /// <summary>Whether the result carries a picture, without building a list of them.</summary>
    public bool HasImages => ToolResultBlocks.HasImage(Blocks);

    public bool Equals(ToolResult? other) =>
        other is not null && IsError == other.IsError && ToolResultBlocks.SequenceEqual(Blocks, other.Blocks);

    public override int GetHashCode() => HashCode.Combine(IsError, ToolResultBlocks.Hash(Blocks));
}

/// <summary>Execution context handed to every tool; the workspace root bounds file access.</summary>
public sealed record ToolContext(string SessionId, string WorkingDirectory, int AgentDepth = 0)
{
    /// <summary>The calling agent instance's id; null when the caller is not an agent loop (a script, a plugin bridge).</summary>
    public string? AgentId { get; init; }

    /// <summary>The calling agent's display name (its definition name, e.g. "main", "scout"); null like <see cref="AgentId"/>.</summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// Where a long-running tool reports what it is doing: each call carries the <em>latest tail</em> of its live output (a
    /// snapshot, not a delta, so a listener may drop calls without losing state). Bounded by the producer, throttled by the
    /// producer, callable from any thread. Null when nobody watches; a tool must not require it.
    /// </summary>
    public Action<string>? Progress { get; init; }

    /// <summary>
    /// The tools the calling agent actually holds (its allow-list after the user's switches), as of the start of the tool batch; null when the caller
    /// is not an agent loop. A tool that must not become a back door around another tool's ownership (<c>bash</c> and git)
    /// asks this instead of guessing from the agent's role.
    /// </summary>
    public IToolRegistry? Tools { get; init; }

    /// <summary>
    /// Hands the caller's parallelism slot back while a tool waits on something it does not control (another agent's
    /// command in the same workspace); disposing the result takes the slot back (with the turn's own token, whatever token is passed in). Null when the caller has no slot.
    /// </summary>
    public Func<CancellationToken, IAsyncDisposable>? YieldSlot { get; init; }

    /// <summary>
    /// The calling agent's file-version ledger, which <c>write</c> consults before overwriting an existing file: no record
    /// means it never read the file, a stale record means someone changed it since. Null when the caller is not an agent loop
    /// (a script, a plugin bridge) - tools must then not check.
    /// </summary>
    public IFileVersionLedger? FileVersions { get; init; }

    /// <summary>
    /// Set on the one call the user allowed across the workspace boundary: the file and shell tools then resolve their paths
    /// outside the workspace instead of refusing them. Never set for a call that stayed inside - their own containment check
    /// is the second gate behind the approval policy, so a policy the caller forgot still cannot write outside.
    /// </summary>
    public bool OutOfWorkspaceAllowed { get; init; }
}

/// <summary>
/// One file's identity, as cheaply as the filesystem can tell it. <paramref name="LastWriteTicks"/> is
/// <c>FileInfo.LastWriteTimeUtc.Ticks</c> (UTC) — mixing local time in makes every read/write comparison mismatch and the
/// overwrite check refuse or miss files wholesale (same convention as the spec-graph's spec cache).
/// </summary>
public readonly record struct FileVersion(long Length, long LastWriteTicks);

/// <summary>
/// The file versions one agent has seen: <c>read</c> records, <c>write</c> compares before overwriting and records after a
/// successful write, <c>edit</c>/<c>append</c> record only when the pre-edit state still matched. Implemented per agent
/// instance, not per session id - a rebuilt agent has not seen what its predecessor read. Thread-safe; callers pass a path
/// already normalized by <c>WorkspacePath.Normalize</c>. Null in <see cref="ToolContext.FileVersions"/> means "do not check".
/// </summary>
public interface IFileVersionLedger
{
    /// <summary>Whether this agent has a record for <paramref name="path"/>; false when it never read the file.</summary>
    bool TryGet(string path, out FileVersion version);

    /// <summary>Records (or replaces) what this agent has seen of <paramref name="path"/>.</summary>
    void Record(string path, FileVersion version);
}

/// <summary>Where a tool comes from. Declared by whoever registers the tool, never reported by the tool itself.</summary>
public enum ToolOrigin
{
    /// <summary>Ships with Forge (Core or App). The user may switch it off in settings.</summary>
    BuiltIn,

    /// <summary>A plugin tool from the extension host (including <c>run_code</c>); switched on and off per plugin.</summary>
    Extension,

    /// <summary>A tool of an MCP server; switched on and off per server.</summary>
    Mcp,
}

/// <summary>
/// Catalog entry for one tool. <paramref name="Group"/> is one of <see cref="ToolGroups"/> for a built-in tool and the
/// plugin or server name otherwise; <paramref name="Source"/> names the plugin or MCP server (null for a built-in tool).
/// <paramref name="CanDisable"/> is false only for a built-in tool another feature cannot run without.
/// </summary>
public sealed record ToolDescriptor(
    string Name,
    ToolOrigin Origin,
    string Group,
    string? Source = null,
    bool CanDisable = true);

/// <summary>Groups of built-in tools, used to lay out the settings page.</summary>
public static class ToolGroups
{
    public const string Files = "files";
    public const string Git = "git";
    public const string PullRequests = "pr";
    public const string Web = "web";
    public const string Session = "session";
    public const string Agents = "agents";
    public const string Retrieval = "retrieval";
    public const string Extensions = "extensions";
}

public interface ITool
{
    ToolDefinition Definition { get; }
    ToolSideEffect SideEffect { get; }
    ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct);
}

/// <summary>
/// A tool that bounds its own result well below the loop's spill threshold and tells the model how to continue (<c>read</c>
/// does: a character cap plus the offset to resume from). The loop never moves such a result into an artifact, so the model
/// is not sent to a second tool just to read what the first one already cut to size.
/// </summary>
public interface IBoundedOutputTool : ITool
{
}

public interface IToolRegistry
{
    IReadOnlyList<ITool> Tools { get; }
    bool TryGet(string name, out ITool tool);
}

/// <summary>
/// Whether one call's paths all stay inside the session's workspace. It is the boundary the approval policy answers on: a
/// read-only call is always allowed, a write or an execution inside the workspace needs no card, and one that names a path
/// outside asks the user once.
/// </summary>
public enum ToolCallScope { InsideWorkspace, OutsideWorkspace }

/// <summary>
/// A tool that can say, before it runs, whether this call would touch a path outside the session's workspace. Implemented by
/// the file and shell tools (<c>Forge.IO</c>), which resolve exactly the paths they later execute; every other tool falls back
/// to <see cref="ToolScopes.Of"/>'s argument scan, which is a best effort at path-shaped arguments and no guess at behaviour.
/// </summary>
public interface IToolCallScope
{
    /// <summary>
    /// The scope of one call. Must not throw and must not touch the disk more than resolving the paths costs: an argument it
    /// cannot read is <see cref="ToolCallScope.OutsideWorkspace"/>, so the user is asked rather than silently skipped.
    /// </summary>
    ToolCallScope ScopeOf(ToolCall call, string workingDirectory);
}

/// <summary>
/// How one call's scope is answered: the tool's own resolution when it has one, otherwise a scan of the arguments for path-shaped
/// values — the only reading available for a plugin or MCP tool, whose arguments are all the main program knows about it.
/// </summary>
public static class ToolScopes
{
    /// <summary>
    /// Key words that mark a string as a path, matched as the start of a key's word: <c>paths</c>, <c>file_path</c>,
    /// <c>filePath</c> and <c>dirname</c> count, <c>profile</c> does not.
    /// </summary>
    private static readonly string[] PathKeyWords =
    [
        "path", "file", "dir", "folder", "cwd", "workdir", "root", "target", "source", "src", "dest", "dst", "input", "output",
    ];

    public static ToolCallScope Of(ITool tool, ToolCall call, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(call);
        if (tool is IToolCallScope scope)
        {
            try
            {
                return scope.ScopeOf(call, workingDirectory);
            }
            catch (Exception)
            {
                return ToolCallScope.OutsideWorkspace;
            }
        }

        return ScanArguments(call.ArgumentsJson, workingDirectory);
    }

    /// <summary>
    /// True when the path is the root itself or below it, compared the way the platform compares paths (no volume probing:
    /// macOS' case-insensitive default therefore counts a differently-cased spelling as outside, which asks instead of allowing).
    /// </summary>
    public static bool IsWithin(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string full;
        string fullRoot;
        try
        {
            full = Trim(Path.GetFullPath(path));
            fullRoot = Trim(Path.GetFullPath(root));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(full, fullRoot, comparison)
               || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison)
               || full.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, comparison);
    }

    private static ToolCallScope ScanArguments(string json, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ToolCallScope.InsideWorkspace;
        }

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return ToolCallScope.OutsideWorkspace;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return ToolCallScope.OutsideWorkspace;
        }

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? Scan(document.RootElement, workingDirectory)
                : ToolCallScope.OutsideWorkspace;
        }
    }

    private static ToolCallScope Scan(JsonElement element, string workingDirectory)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var scope = LooksLikePathKey(property.Name)
                        ? ScanValue(property.Value, workingDirectory)
                        : Scan(property.Value, workingDirectory);
                    if (scope == ToolCallScope.OutsideWorkspace)
                    {
                        return scope;
                    }
                }

                return ToolCallScope.InsideWorkspace;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (Scan(item, workingDirectory) == ToolCallScope.OutsideWorkspace)
                    {
                        return ToolCallScope.OutsideWorkspace;
                    }
                }

                return ToolCallScope.InsideWorkspace;
            default:
                return ToolCallScope.InsideWorkspace;
        }
    }

    /// <summary>One path-shaped value: a string, or an array of them; anything else is inspected as plain JSON.</summary>
    private static ToolCallScope ScanValue(JsonElement value, string workingDirectory)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return NamesOutside(value.GetString(), workingDirectory) ? ToolCallScope.OutsideWorkspace : ToolCallScope.InsideWorkspace;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String
                        ? NamesOutside(item.GetString(), workingDirectory)
                        : Scan(item, workingDirectory) == ToolCallScope.OutsideWorkspace)
                    {
                        return ToolCallScope.OutsideWorkspace;
                    }
                }

                return ToolCallScope.InsideWorkspace;
            default:
                return Scan(value, workingDirectory);
        }
    }

    /// <summary>
    /// True when a path argument can only mean something outside the workspace: home-relative (the tools never expand <c>~</c>), or
    /// rooted or escaping upwards by a <c>..</c> segment and resolving outside, or a path the platform refuses to parse. The test is
    /// lexical - it reads no directory - so a plain relative name is inside; a link inside the workspace that points outside is left
    /// to the tool's own containment check, which then answers with an error rather than a card.
    /// </summary>
    public static bool NamesOutside(string? value, string workingDirectory)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (trimmed[0] == '~')
        {
            return true;
        }

        var rooted = Path.IsPathRooted(trimmed);
        if (!rooted && !HasParentSegment(trimmed))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(rooted ? trimmed : Path.Combine(workingDirectory, trimmed));
            return !IsWithin(full, workingDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private static bool HasParentSegment(string value)
    {
        var span = value.AsSpan();
        var start = 0;
        for (var index = 0; index <= span.Length; index++)
        {
            if (index < span.Length && span[index] is not ('/' or '\\'))
            {
                continue;
            }

            if (index - start == 2 && span[start] == '.' && span[start + 1] == '.')
            {
                return true;
            }

            start = index + 1;
        }

        return false;
    }

    /// <summary>True when a property name has a path word among its parts (<c>file_path</c>, <c>filePath</c>, <c>target</c>).</summary>
    private static bool LooksLikePathKey(string name)
    {
        var start = 0;
        for (var index = 0; index <= name.Length; index++)
        {
            var boundary = index == name.Length
                           || name[index] is '_' or '-' or '.'
                           || (index > start && char.IsUpper(name[index]));
            if (!boundary)
            {
                continue;
            }

            if (index - start > 0 && StartsWithPathWord(name.AsSpan(start, index - start)))
            {
                return true;
            }

            start = index;
            if (index < name.Length && name[index] is '_' or '-' or '.')
            {
                start = index + 1;
            }
        }

        return false;
    }

    private static bool StartsWithPathWord(ReadOnlySpan<char> word)
    {
        foreach (var key in PathKeyWords)
        {
            if (word.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Trim(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

public enum ApprovalMode { Allow, Ask, Deny }
public enum ApprovalDecision { Allow, Deny }

/// <summary>
/// The user's global tool permission setting (<c>settings.json</c> <c>preferences.toolsPermission</c>), read on every evaluation
/// so a change reaches the next call of every session.
/// </summary>
public enum ToolPermissionMode
{
    /// <summary>Writes and executions are free inside the session's workspace; one that names a path outside asks once.</summary>
    Workspace,

    /// <summary>Every call runs without a card. Plugin approvals and deletion confirmations are not tool permissions and still ask.</summary>
    All,
}

/// <summary>
/// One approval ask. <paramref name="Scope"/> is that call's boundary, answered by <see cref="ToolScopes.Of"/> before the
/// policy runs; it has no default so a caller cannot forget it and get a silent allow - a call whose arguments name no path but
/// whose reach is known to be the app's own state (a plugin command, say) says <see cref="ToolCallScope.InsideWorkspace"/> out
/// loud, next to the reason. The policy allows a call inside the workspace and asks about one outside, and only an allowed
/// outside call carries <see cref="ToolContext.OutOfWorkspaceAllowed"/> into the tool.
/// <para><paramref name="RunKey"/> is the run a <c>run_code</c> script's inner tool call belongs to, or null
/// for every other caller (the agent loop, plugin commands, plugin executions): a non-null key lets the session's approval
/// handler serialize the calls of one run for one tool and offer "allow the rest of this run". It is opaque to the
/// contract and unique per run, so a stale grant can never match a later one, and it never crosses IPC - the request is
/// built in the main program after the wire message was matched against the trusted run record.</para>
/// </summary>
public sealed record ApprovalRequest(
    string ToolName,
    string ArgumentsJson,
    ToolSideEffect SideEffect,
    string WorkingDirectory,
    ToolCallScope Scope,
    string? RunKey = null);

/// <summary>Pure policy evaluation; configurable per tool/directory in settings.</summary>
public interface IApprovalPolicy
{
    ApprovalMode Evaluate(ApprovalRequest request);
}

/// <summary>Interactive side of approval; implemented by the UI layer.</summary>
public interface IApprovalHandler
{
    ValueTask<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct);
}
