using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>
/// Shared plumbing for the git tools: one JSON-argument parse per call, unknown or mistyped properties refused before
/// git runs, and exceptions (except cancellation) turned into tool errors.
/// </summary>
internal abstract class GitToolBase : ITool
{
    protected GitToolBase(GitCli git, string name, string description, string schema, ToolSideEffect sideEffect)
    {
        Git = git;
        Definition = new ToolDefinition(name, description, schema);
        SideEffect = sideEffect;
    }

    protected GitCli Git { get; }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect { get; }

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
                return await InvokeCoreAsync(document.RootElement, context, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new ToolResult($"{Definition.Name} failed: {exception.Message}", IsError: true);
            }
        }
    }

    protected abstract ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct);

    /// <summary>One line describing the branch, its upstream and how far it diverged as of the last fetch.</summary>
    protected static string BranchLine(GitBranchStatus status)
    {
        var text = new StringBuilder(96);
        if (status.IsDetached)
        {
            text.Append("HEAD is detached");
            if (status.HeadCommit is { } detachedHead)
            {
                text.Append(" at ").Append(detachedHead);
            }

            return text.Append('.').ToString();
        }

        text.Append("Branch ").Append(status.Branch ?? "(unknown)");
        if (status.Upstream is null)
        {
            return text.Append(", no upstream.").ToString();
        }

        text.Append(", upstream ").Append(status.Upstream);
        if (status.UpstreamGone)
        {
            return text.Append(" (gone: the remote branch no longer exists).").ToString();
        }

        if (status.Ahead is { } ahead && status.Behind is { } behind)
        {
            text.Append(", ahead ").Append(ahead).Append(", behind ").Append(behind);
        }

        return text.Append(" (as of the last fetch).").ToString();
    }

    /// <summary>Per-call network timeout, read from the live settings; zero or negative means "use the git default".</summary>
    protected static GitNetworkOptions NetworkOptions(GitToolSettings settings)
    {
        var timeout = settings.NetworkTimeout();
        return timeout > TimeSpan.Zero ? new GitNetworkOptions { Timeout = timeout } : new GitNetworkOptions();
    }

    /// <summary>Reports a branch or remote outcome as the tool result: the message on success, a named failure otherwise.</summary>
    protected static ToolResult Report(GitRemoteResult result) =>
        result.Success ? new ToolResult(result.Message) : GitToolFailure.ToToolResult(result);
}

/// <summary>Strict readers for tool arguments; a property of the wrong JSON type is an error, never a silent default.</summary>
internal static class GitToolJson
{
    /// <summary>Refuses anything but a JSON object and rejects properties the tool does not declare.</summary>
    public static void EnsureObject(JsonElement arguments, params string[] allowed)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Arguments must be a JSON object.");
        }

        foreach (var property in arguments.EnumerateObject())
        {
            var known = false;
            foreach (var name in allowed)
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                throw new ArgumentException(
                    allowed.Length == 0
                        ? $"Unknown property '{property.Name}'; this tool takes no arguments."
                        : $"Unknown property '{property.Name}'; allowed properties: {string.Join(", ", allowed)}.");
            }
        }
    }

    public static string? String(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new ArgumentException($"'{name}' must be a string.");
    }

    public static string RequiredString(JsonElement root, string name) =>
        String(root, name) is { Length: > 0 } text
            ? text
            : throw new ArgumentException($"'{name}' must be a non-empty string.");

    public static bool Boolean(JsonElement root, string name, bool fallback)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException($"'{name}' must be a boolean."),
        };
    }

    public static int Integer(JsonElement root, string name, int fallback)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw new ArgumentException($"'{name}' must be an integer.");
        }

        return number;
    }

    public static IReadOnlyList<string> Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"'{name}' must be an array of strings.");
        }

        var items = new List<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ArgumentException($"'{name}' must be an array of strings.");
            }

            items.Add(item.GetString()!);
        }

        return items;
    }
}

/// <summary>Turns a classified git failure into the tool error the model reads: the kind, what happened, and the next step.</summary>
internal static class GitToolFailure
{
    public static ToolResult ToToolResult(GitRemoteResult result, string? hint = null)
    {
        var text = new StringBuilder(256);
        text.Append(FailureName(result.Failure)).Append(": ").Append(result.Message);
        var next = hint ?? DefaultHint(result.Failure);
        if (next.Length > 0)
        {
            text.Append("\nNext: ").Append(next);
        }

        return new ToolResult(text.ToString(), IsError: true);
    }

    /// <summary>Same reporting for a typed helper that reported the failure as an exception.</summary>
    public static ToolResult FromException(GitException exception, string message, string? hint = null) =>
        ToToolResult(
            new GitRemoteResult(
                GitFailureClassifier.Classify(new GitCommandResult(exception.ExitCode, string.Empty, exception.StandardError)),
                message + " " + exception.Message,
                exception.StandardError),
            hint);

    public static string FailureName(GitRemoteFailureKind kind) => kind switch
    {
        GitRemoteFailureKind.AuthenticationFailed => nameof(GitRemoteFailureKind.AuthenticationFailed),
        GitRemoteFailureKind.NonFastForward => nameof(GitRemoteFailureKind.NonFastForward),
        GitRemoteFailureKind.Conflict => nameof(GitRemoteFailureKind.Conflict),
        GitRemoteFailureKind.RemoteNotFound => nameof(GitRemoteFailureKind.RemoteNotFound),
        GitRemoteFailureKind.NoUpstream => nameof(GitRemoteFailureKind.NoUpstream),
        GitRemoteFailureKind.DirtyWorkTree => nameof(GitRemoteFailureKind.DirtyWorkTree),
        GitRemoteFailureKind.Network => nameof(GitRemoteFailureKind.Network),
        GitRemoteFailureKind.TimedOut => nameof(GitRemoteFailureKind.TimedOut),
        _ => nameof(GitRemoteFailureKind.Other),
    };

    public static string DefaultHint(GitRemoteFailureKind kind) => kind switch
    {
        GitRemoteFailureKind.AuthenticationFailed =>
            "git cannot prompt for credentials here. Configure an ssh key or a credential helper for this remote, then retry.",
        GitRemoteFailureKind.NonFastForward =>
            "The remote moved. Fetch, then rebase or merge the remote branch and push again; never force without a fresh lease.",
        GitRemoteFailureKind.Conflict =>
            "Read both sides' history with git log -p before resolving; never mechanically take one side. Stage the " +
            "resolved files, then run git_sync continue (or git_sync abort).",
        GitRemoteFailureKind.RemoteNotFound =>
            "Check the remote name with git_status, and add a missing remote with git remote add.",
        GitRemoteFailureKind.NoUpstream =>
            "Set an upstream first: git_branch set_upstream, or push with set_upstream true.",
        GitRemoteFailureKind.DirtyWorkTree => "Commit or stash the tracked changes first.",
        GitRemoteFailureKind.Network =>
            "The host could not be reached. Check the connection, the remote URL and any proxy, then retry.",
        GitRemoteFailureKind.TimedOut =>
            "The call hit the network timeout. Retry, or raise the git network timeout in settings.",
        _ => string.Empty,
    };
}

/// <summary>
/// Renders a parsed <see cref="FileDiff"/> back as unified diff text, which is how untracked files (that git itself
/// never prints) appear in the <c>git_diff</c> output.
/// </summary>
internal static class UnifiedDiffText
{
    public static string Render(FileDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var path = diff.Path;
        var text = new StringBuilder(256);
        text.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n');

        if (diff.Status == FileDiffStatus.Added)
        {
            text.Append("new file mode ").Append(diff.NewMode ?? "100644").Append('\n');
        }
        else if (diff.Status == FileDiffStatus.Deleted)
        {
            text.Append("deleted file mode ").Append(diff.OldMode ?? "100644").Append('\n');
        }

        if (diff.IsBinary)
        {
            return text.Append("Binary files a/").Append(path).Append(" and b/").Append(path).Append(" differ\n").ToString();
        }

        text.Append("--- ").Append(diff.Status == FileDiffStatus.Added ? "/dev/null" : "a/" + path).Append('\n');
        text.Append("+++ ").Append(diff.Status == FileDiffStatus.Deleted ? "/dev/null" : "b/" + path).Append('\n');

        foreach (var hunk in diff.Hunks)
        {
            text.Append(hunk.Header).Append('\n');
            foreach (var line in hunk.Lines)
            {
                text.Append(line.Kind switch
                {
                    DiffLineKind.Added => '+',
                    DiffLineKind.Removed => '-',
                    _ => ' ',
                }).Append(line.Text).Append('\n');

                if (line.NoNewLineAtEnd)
                {
                    text.Append("\\ No newline at end of file\n");
                }
            }
        }

        if (diff.IsTruncated)
        {
            text.Append("… [truncated: ")
                .Append(path)
                .Append(" is larger than the untracked-file read limit; only the first part is shown]\n");
        }

        return text.ToString();
    }

    /// <summary>One <c>--stat</c>-style line per file plus a count line, for the compact <c>stat_only</c> read.</summary>
    public static void AppendStat(StringBuilder text, IReadOnlyList<FileDiff> files)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(files);

        var added = 0;
        var removed = 0;
        foreach (var file in files)
        {
            added += file.AddedLineCount;
            removed += file.RemovedLineCount;
            text.Append("  ");
            if (file.IsRenameOrCopy)
            {
                text.Append(file.OldPath).Append(" => ").Append(file.NewPath);
            }
            else
            {
                text.Append(file.Path);
            }

            text.Append(" | ");
            if (file.IsBinary)
            {
                text.Append("binary");
            }
            else
            {
                text.Append('+').Append(file.AddedLineCount.ToString(CultureInfo.InvariantCulture))
                    .Append(" -").Append(file.RemovedLineCount.ToString(CultureInfo.InvariantCulture));
            }

            text.Append('\n');
        }

        if (files.Count == 0)
        {
            text.Append("  (no changes)\n");
        }
        else
        {
            text.Append("  ").Append(files.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" file(s), +").Append(added.ToString(CultureInfo.InvariantCulture))
                .Append(" -").Append(removed.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }
}
