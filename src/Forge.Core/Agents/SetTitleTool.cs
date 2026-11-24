using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// Names the conversation the model is working in (<c>set_title</c>). The host writes the name, so the model never touches
/// the store, and it decides when: the name lands only in the session's first turn, a name a person typed is never replaced,
/// and a name the model already wrote stays put - a late or repeated call is harmless instead of a fight over the label.
/// <para>It carries no file, network or git effect: the worktree branch and directory keep the name they were created with,
/// so a rename can never move a checkout out from under a review or a commit.</para>
/// </summary>
public sealed class SetTitleTool : ITool
{
    public const string Name = "set_title";

    private const string SchemaJson =
        """{"type":"object","properties":{"title":{"type":"string","description":"3-6 words naming this conversation in the user's language, like a commit subject but about the task."}},"required":["title"],"additionalProperties":false}""";

    private const string Description =
        "Name this conversation, once, in this chat's first turn: the tab and the tree show the name, and the host fills a placeholder "
        + "from the first message, so this replaces that placeholder with the real subject. "
        + "Write it in the user's language: 3-6 words, the outcome rather than the process (\"修复登录跳转\", not \"看一下\"); the "
        + "host cuts a longer one down to one tab label. "
        + "A name a person typed, one this tool already set, or the name an earlier turn settled on is kept - the result says so, "
        + "and then do not call it again. "
        + "It renames nothing on disk: the worktree branch and directory are fixed at creation.";

    private readonly Func<string, CancellationToken, ValueTask<string?>> rename;

    /// <param name="rename">Writes the name to this session and answers the name it now shows - the host cuts it to one tab
    /// label - or null when a person, an earlier call or the session's first turn already settled it.</param>
    public SetTitleTool(Func<string, CancellationToken, ValueTask<string?>> rename)
    {
        ArgumentNullException.ThrowIfNull(rename);
        this.rename = rename;
    }

    public ToolDefinition Definition { get; } = new(Name, Description, SchemaJson);

    /// <summary>A write, but session state rather than workspace state: the session policy allows it without a card.</summary>
    public ToolSideEffect SideEffect => ToolSideEffect.Write;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);

        string? raw;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            raw = JsonArguments.OptionalString(document.RootElement, "title");
        }
        catch (JsonException exception)
        {
            return new ToolResult($"{Name} arguments are invalid: {exception.Message}", IsError: true);
        }

        // Every instance of a host holds this tool, and the seam writes the host's own session: a sub-agent naming the
        // conversation would rename the one that spawned it.
        if (context.AgentDepth > 0)
        {
            return new ToolResult($"{Name}: only the root agent names this conversation.", IsError: true);
        }

        if (Fold(raw) is not { } title)
        {
            return new ToolResult($"{Name}: title must contain letters or digits.", IsError: true);
        }

        return await rename(title, ct).ConfigureAwait(false) is { } written
            ? new ToolResult($"Chat title set to \"{written}\".")
            : new ToolResult("Chat title kept: this chat's name is already settled.");
    }

    /// <summary>Folds a name onto one line and rejects one with nothing readable in it; the host cuts the length, because the
    /// budget belongs to the label that shows it rather than to this tool.</summary>
    internal static string? Fold(string? raw)
    {
        var text = string.Join(' ', (raw ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 || !text.Any(char.IsLetterOrDigit) ? null : text;
    }
}
