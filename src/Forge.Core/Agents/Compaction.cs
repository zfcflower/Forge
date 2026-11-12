using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Storage;

namespace Forge.Core.Agents;

public sealed record CompactionResult(int TokensBefore, int TokensAfter);

/// <summary>Why a compaction runs; the compactor treats them alike apart from what the caller put in the request.</summary>
public enum CompactionReason { Auto, Overflow, Manual }

/// <summary>
/// One compaction call. <see cref="TargetTokens"/> is what the whole context should aim at (1 = as small as the kept tail
/// allows); <see cref="KeepRecentTokens"/> is how much of the newest raw history stays verbatim at least (see
/// <see cref="SummaryLayers.PlanCut"/> for how both meet at message granularity); <see cref="CustomInstructions"/> is the
/// user's focus text from <c>/compact &lt;text&gt;</c>, appended to the summarizer prompts.
/// </summary>
public sealed record CompactionRequest(
    int TargetTokens,
    int KeepRecentTokens,
    CompactionReason Reason,
    string? CustomInstructions = null);

/// <summary>
/// The summarizer did not produce a usable summary (the request failed, was cut off at the output limit, answered with a
/// tool call or with nothing). Nothing was persisted and the context is unchanged.
/// </summary>
public sealed class CompactionFailedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Rewrites an instance's in-memory context to fit a token budget without losing stored data.</summary>
public interface IContextCompactor
{
    /// <summary>
    /// Compacts one instance as <paramref name="request"/> asks; null means nothing was worth compacting. Throws
    /// <see cref="CompactionFailedException"/> when a summary was needed but none usable came back.
    /// </summary>
    Task<CompactionResult?> CompactAsync(AgentInstance instance, CompactionRequest request, CancellationToken ct);
}

public sealed class NoopCompactor : IContextCompactor
{
    public static NoopCompactor Instance { get; } = new();

    public Task<CompactionResult?> CompactAsync(AgentInstance instance, CompactionRequest request, CancellationToken ct) =>
        Task.FromResult<CompactionResult?>(null);
}

/// <summary>
/// Checkpoint compaction over <see cref="SummaryLayers"/>. Each compaction cuts the raw tail so that its newest part
/// (at least <see cref="CompactionRequest.KeepRecentTokens"/>, more when the target allows) stays verbatim, and writes
/// one new layer-2 record covering the session from its first message through the cut: the structured summary of the
/// previous summary updated with the newly cut messages, plus the files read and modified so far. The context becomes
/// [inherited prefix] + [that summary] + [raw tail]. A cut inside one long interaction summarizes the turn's prefix
/// separately and appends it as turn context. Records are only ever appended; summarized originals stay in the store.
/// </summary>
public sealed class SummaryCompactor : IContextCompactor
{
    /// <summary>System prompt of every summarizer request (the <c>compaction.summarizer</c> slot's built-in text).</summary>
    public const string SummarizerPrompt =
        """
        You are a context summarization assistant for a coding agent. You read a conversation between a user and the agent, given inside <conversation> tags, and write a structured checkpoint summary that lets the agent continue the work without the original messages.
        Do not continue the conversation and do not answer any question in it: output only the summary.

        Use this format exactly:

        ## Goal
        [What the user is trying to accomplish; a short list when there are several goals.]

        ## Constraints & Preferences
        - [Requirements, constraints and preferences the user stated, or "(none)"]

        ## Progress
        ### Done
        - [x] [Completed work]

        ### In Progress
        - [ ] [Work under way]

        ### Blocked
        - [Open problems, or "(none)"]

        ## Key Decisions
        - **[Decision]**: [Rationale]

        ## Next Steps
        1. [What should happen next, in order]

        ## Critical Context
        - [Data, examples, references and facts needed to continue, or "(none)"]

        Keep every section concise. Preserve exact file paths, function names and error messages.
        """;

    /// <summary>Instruction for a first summary (no previous one).</summary>
    public const string InitialInstruction =
        "The messages above are a conversation to summarize. Write the structured checkpoint summary in the required format.";

    /// <summary>Instruction for updating a previous summary with newly compacted messages.</summary>
    public const string UpdateInstruction =
        """
        The messages above are NEW conversation messages to incorporate into the existing summary given in <previous-summary>.
        Update that summary:
        - Preserve all information from it that still applies.
        - Add the progress, decisions and context from the new messages.
        - Move items from "In Progress" to "Done" when they were completed.
        - Update "Next Steps" to match what was accomplished.
        - Drop only what the new messages made obsolete.
        Write the complete updated summary in the same format. Preserve exact file paths, function names and error messages.
        """;

    /// <summary>Instruction for the prefix of one interaction that is too large to keep whole.</summary>
    public const string TurnPrefixInstruction =
        """
        The messages above are the PREFIX of a single turn that is too large to keep whole; its later part stays in the context verbatim.
        For this request do not use the checkpoint format. Summarize the prefix so the kept later part can be understood, in exactly this format:

        ## Original Request
        [What the user asked for in this turn]

        ## Progress So Far
        - [Work done and results obtained in the prefix]

        ## Context Needed to Continue
        - [Facts, file paths, function names and error messages the kept part relies on]

        Be concise.
        """;

    /// <summary>Separator between the history summary and the summary of a split turn's prefix.</summary>
    public const string SplitTurnHeading = "**Turn Context (split turn):**";

    public const string ReadFilesTag = "read-files";
    public const string ModifiedFilesTag = "modified-files";

    /// <summary>Largest output requested for the history summary and for a turn-prefix summary.</summary>
    public const int SummaryMaxOutputTokens = 8_192;
    public const int TurnPrefixMaxOutputTokens = 4_096;

    /// <summary>Longest tool result passed to the summarizer, and the longest single tool-call argument.</summary>
    public const int MaxToolResultChars = 2_000;
    public const int MaxToolArgumentChars = 500;

    private const int MaxMessageChars = 8_000;
    private const int MaxConversationChars = 400_000;

    /// <summary>Built-in file tools by name: whether a call reads or modifies the file, and the argument holding its path.</summary>
    private static readonly FrozenDictionary<string, (bool Modifies, string PathArgument)> FileTools =
        new Dictionary<string, (bool Modifies, string PathArgument)>(StringComparer.Ordinal)
        {
            ["read"] = (false, "path"),
            ["edit"] = (true, "path"),
            ["write"] = (true, "path"),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly ISessionStore store;
    private readonly Func<ModelSettings, IChatProvider> providers;
    private readonly ITokenCounter tokens;
    private readonly string summarizerPrompt;

    public SummaryCompactor(
        ISessionStore store,
        Func<ModelSettings, IChatProvider> providers,
        ITokenCounter tokens,
        string? summarizerPrompt = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(tokens);
        this.store = store;
        this.providers = providers;
        this.tokens = tokens;
        this.summarizerPrompt = string.IsNullOrWhiteSpace(summarizerPrompt) ? SummarizerPrompt : summarizerPrompt;
    }

    public async Task<CompactionResult?> CompactAsync(AgentInstance instance, CompactionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.TargetTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(request.KeepRecentTokens);
        if (instance.Messages.Count == 0)
        {
            return null;
        }

        var before = tokens.Count(instance.History);
        var lastSeq = await store.LastSeqAsync(instance.SessionId, ct).ConfigureAwait(false);
        if (lastSeq == 0)
        {
            return null;
        }

        var view = SummaryLayers.Build(await store.GetSummariesAsync(instance.SessionId, ct).ConfigureAwait(false), lastSeq);
        var raw = await ReadRawAsync(instance.SessionId, view.RawFromSeq, lastSeq, ct).ConfigureAwait(false);

        // Full inheritance keeps the inherited prefix whole (it is what makes the parent's prompt cache reusable), so only
        // this session's own messages are summarized and they get the budget the inherited part and the current summary
        // leave; the new summary replaces the current one, so its size is the best guess for the new one.
        var summaryTokens = tokens.Count(SessionHistory.RenderSummaries(view));
        var rawBudget = request.TargetTokens - InheritedTokens(instance) - summaryTokens;
        if (SummaryLayers.PlanCut(raw, rawBudget, request.KeepRecentTokens, CountMessage) is not { } cut)
        {
            return null;
        }

        var previous = Previous(view);
        var read = new SortedSet<string>(previous.Read, StringComparer.Ordinal);
        var modified = new SortedSet<string>(previous.Modified, StringComparer.Ordinal);
        CollectFiles(raw, cut.KeepFrom, read, modified);

        var focus = string.IsNullOrWhiteSpace(request.CustomInstructions)
            ? string.Empty
            : string.Concat("\n\nAdditional focus: ", request.CustomInstructions.Trim());
        var history = cut.HistoryEnd > 0
            ? SummarizeAsync(
                instance,
                UserPrompt(raw, 0, cut.HistoryEnd, previous.Text, previous.Text.Length > 0 ? UpdateInstruction : InitialInstruction, focus),
                SummaryMaxOutputTokens,
                ct)
            : Task.FromResult(previous.Text);
        var prefix = cut.IsSplitTurn
            ? SummarizeAsync(
                instance,
                UserPrompt(raw, cut.TurnStart, cut.KeepFrom, string.Empty, TurnPrefixInstruction, focus),
                TurnPrefixMaxOutputTokens,
                ct)
            : Task.FromResult(string.Empty);
        await Task.WhenAll(history, prefix).ConfigureAwait(false);

        var text = Compose(await history.ConfigureAwait(false), await prefix.ConfigureAwait(false), read, modified);
        var fromSeq = view.SessionSummary?.FromSeq
                      ?? (view.StageSummaries.Count > 0 ? Math.Min(view.StageSummaries[0].FromSeq, raw[0].Seq) : raw[0].Seq);
        await store.PutSummaryAsync(
            new SummaryRecord(instance.SessionId, SummaryLayers.SessionLayer, fromSeq, raw[cut.KeepFrom - 1].Seq, text, DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        var updated = SummaryLayers.Build(await store.GetSummariesAsync(instance.SessionId, ct).ConfigureAwait(false), lastSeq);
        instance.ReplaceHistory(Rebuild(instance, updated));
        return new CompactionResult(before, tokens.Count(instance.History));
    }

    private int CountMessage(ChatMessage message) => tokens.Count([message]);

    private async Task<List<StoredMessage>> ReadRawAsync(string sessionId, long fromSeq, long lastSeq, CancellationToken ct)
    {
        var raw = new List<StoredMessage>();
        if (fromSeq > lastSeq)
        {
            return raw;
        }

        await foreach (var message in store.ReadAsync(sessionId, fromSeq, lastSeq, ct).ConfigureAwait(false))
        {
            raw.Add(message);
        }

        return raw;
    }

    private int InheritedTokens(AgentInstance instance)
    {
        List<ChatMessage>? inherited = null;
        foreach (var entry in instance.Messages)
        {
            if (entry.Inherited)
            {
                (inherited ??= []).Add(entry.Message);
            }
        }

        return inherited is null ? 0 : tokens.Count(inherited);
    }

    /// <summary>[inherited prefix, unchanged] + [own summary view] + [own messages after the last summarized sequence].</summary>
    private static List<AgentMessage> Rebuild(AgentInstance instance, ContextView view)
    {
        var inherited = new List<AgentMessage>();
        var recent = new List<AgentMessage>();
        foreach (var entry in instance.Messages)
        {
            if (entry.Inherited)
            {
                inherited.Add(entry);
            }
            else if (entry.Seq > view.LastSummarizedSeq)
            {
                recent.Add(entry);
            }
        }

        while (recent.Count > 0 && recent[0].Message.Role == MessageRole.Tool)
        {
            recent.RemoveAt(0);
        }

        var rebuilt = new List<AgentMessage>(inherited.Count + recent.Count + 1);
        rebuilt.AddRange(inherited);
        var summaryText = SessionHistory.RenderSummaries(view);
        if (summaryText.Length > 0)
        {
            rebuilt.Add(new AgentMessage(ChatMessage.User(summaryText), 0));
        }

        rebuilt.AddRange(recent);
        return rebuilt;
    }

    // ------------------------------------------------------------------ previous summary and file lists

    private readonly record struct PreviousSummary(string Text, List<string> Read, List<string> Modified);

    /// <summary>
    /// The summary the new one updates, without its file lists: the session summary, followed by the stage summaries a
    /// session compacted by an earlier version may still have.
    /// </summary>
    private static PreviousSummary Previous(ContextView view)
    {
        var read = new List<string>();
        var modified = new List<string>();
        var builder = new StringBuilder(2048);
        if (view.SessionSummary is { } session)
        {
            AppendBody(builder, session.Text, read, modified);
        }

        foreach (var stage in view.StageSummaries)
        {
            AppendBody(builder, stage.Text, read, modified);
        }

        return new PreviousSummary(builder.ToString(), read, modified);
    }

    private static void AppendBody(StringBuilder builder, string text, List<string> read, List<string> modified)
    {
        var body = ExtractFileList(text, ModifiedFilesTag, modified);
        body = ExtractFileList(body, ReadFilesTag, read).Trim();
        if (body.Length == 0)
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append("\n\n");
        }

        builder.Append(body);
    }

    /// <summary>Removes the last <c>&lt;tag&gt;…&lt;/tag&gt;</c> block from <paramref name="text"/> and adds its lines to <paramref name="into"/>.</summary>
    internal static string ExtractFileList(string text, string tag, List<string> into)
    {
        var open = string.Concat("<", tag, ">");
        var close = string.Concat("</", tag, ">");
        var start = text.LastIndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return text;
        }

        var end = text.IndexOf(close, start + open.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            return text;
        }

        foreach (var line in text.AsSpan(start + open.Length, end - start - open.Length).EnumerateLines())
        {
            var path = line.Trim();
            if (!path.IsEmpty)
            {
                into.Add(path.ToString());
            }
        }

        return string.Concat(text.AsSpan(0, start), text.AsSpan(end + close.Length));
    }

    /// <summary>Paths the built-in file tools read or modified in <c>raw[0, end)</c>; unknown tools are ignored.</summary>
    internal static void CollectFiles(IReadOnlyList<StoredMessage> raw, int end, ISet<string> read, ISet<string> modified)
    {
        for (var index = 0; index < end; index++)
        {
            var message = raw[index].Message;
            if (message.Role != MessageRole.Assistant)
            {
                continue;
            }

            foreach (var block in message.Content)
            {
                if (block is ToolCallBlock call
                    && FileTools.TryGetValue(call.Name, out var tool)
                    && PathArgument(call.ArgumentsJson, tool.PathArgument) is { } path)
                {
                    (tool.Modifies ? modified : read).Add(path);
                }
            }
        }
    }

    private static string? PathArgument(string argumentsJson, string name)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String
                   && value.GetString() is { Length: > 0 } path
                ? path.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>History summary, the split turn's prefix summary when there is one, then the file lists.</summary>
    internal static string Compose(string history, string turnPrefix, SortedSet<string> read, SortedSet<string> modified)
    {
        var builder = new StringBuilder(history.Length + turnPrefix.Length + 256);
        builder.Append(history.Trim());
        if (turnPrefix.Length > 0)
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n---\n\n");
            }

            builder.Append(SplitTurnHeading).Append("\n\n").Append(turnPrefix.Trim());
        }

        read.ExceptWith(modified);
        AppendFileList(builder, ReadFilesTag, read);
        AppendFileList(builder, ModifiedFilesTag, modified);
        return builder.ToString();
    }

    private static void AppendFileList(StringBuilder builder, string tag, SortedSet<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        builder.Append("\n\n<").Append(tag).Append(">\n");
        foreach (var path in paths)
        {
            builder.Append(path).Append('\n');
        }

        builder.Append("</").Append(tag).Append('>');
    }

    // ------------------------------------------------------------------ summarizer request

    /// <summary>
    /// <c>&lt;conversation&gt;</c> (raw[from, to) serialized), the optional <c>&lt;previous-summary&gt;</c>, then the
    /// instruction and the user's focus.
    /// </summary>
    internal static string UserPrompt(IReadOnlyList<StoredMessage> raw, int from, int to, string previous, string instruction, string focus)
    {
        var builder = new StringBuilder(8192);
        builder.Append("<conversation>\n");
        SerializeConversation(builder, raw, from, to);
        builder.Append("\n</conversation>\n\n");
        if (previous.Length > 0)
        {
            builder.Append("<previous-summary>\n").Append(previous).Append("\n</previous-summary>\n\n");
        }

        builder.Append(instruction).Append(focus);
        return builder.ToString();
    }

    /// <summary>
    /// <c>[User]: …</c>, <c>[Assistant]: …</c>, <c>[Assistant tool calls]: name(arg=value, …)</c> and <c>[Tool result]: …</c>
    /// lines; reasoning is left out, tool results are cut at <see cref="MaxToolResultChars"/>, images and files appear as
    /// placeholders.
    /// </summary>
    internal static void SerializeConversation(StringBuilder builder, IReadOnlyList<StoredMessage> raw, int from, int to)
    {
        var start = builder.Length;
        for (var index = from; index < to; index++)
        {
            if (builder.Length - start >= MaxConversationChars)
            {
                builder.Append("\n\n[remaining messages omitted for length]");
                return;
            }

            var message = raw[index].Message;
            switch (message.Role)
            {
                case MessageRole.User:
                case MessageRole.System:
                    AppendLine(builder, "[User]: ", Truncate(Prose(message), MaxMessageChars));
                    break;
                case MessageRole.Assistant:
                    AppendLine(builder, "[Assistant]: ", Truncate(Prose(message), MaxMessageChars));
                    AppendLine(builder, "[Assistant tool calls]: ", ToolCalls(message));
                    break;
                case MessageRole.Tool:
                    foreach (var block in message.Content)
                    {
                        switch (block)
                        {
                            case ToolResultBlock result:
                                AppendLine(builder, result.IsError ? "[Tool result (error)]: " : "[Tool result]: ", Truncate(result.Content, MaxToolResultChars));
                                break;
                            case ArtifactRefBlock artifact:
                                AppendLine(builder, "[Tool result]: ", ArtifactPlaceholder(artifact));
                                break;
                            case TextBlock text:
                                AppendLine(builder, "[Tool result]: ", Truncate(text.Text, MaxToolResultChars));
                                break;
                        }
                    }

                    break;
            }
        }
    }

    private static void AppendLine(StringBuilder builder, string label, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append("\n\n");
        }

        builder.Append(label).Append(text);
    }

    private static string Prose(ChatMessage message)
    {
        var builder = new StringBuilder(256);
        foreach (var block in message.Content)
        {
            var text = block switch
            {
                TextBlock value => value.Text,
                ImageBlock image => MessageJson.ImagePlaceholder(image),
                FileBlock file => MessageJson.FilePlaceholder(file),
                ArtifactRefBlock artifact => ArtifactPlaceholder(artifact),
                _ => null,
            };
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    private static string ToolCalls(ChatMessage message)
    {
        StringBuilder? builder = null;
        foreach (var block in message.Content)
        {
            if (block is not ToolCallBlock call)
            {
                continue;
            }

            builder ??= new StringBuilder(256);
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(call.Name).Append('(');
            AppendArguments(builder, call.ArgumentsJson);
            builder.Append(')');
        }

        return builder?.ToString() ?? string.Empty;
    }

    private static void AppendArguments(StringBuilder builder, string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                builder.Append(Truncate(argumentsJson, MaxToolArgumentChars));
                return;
            }

            var first = true;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!first)
                {
                    builder.Append(", ");
                }

                first = false;
                builder.Append(property.Name).Append('=').Append(Truncate(property.Value.GetRawText(), MaxToolArgumentChars));
            }
        }
        catch (JsonException)
        {
            builder.Append(Truncate(argumentsJson, MaxToolArgumentChars));
        }
    }

    internal static string Truncate(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        return string.Concat(
            text.AsSpan(0, limit),
            "\n[… ",
            (text.Length - limit).ToString(CultureInfo.InvariantCulture),
            " chars truncated]");
    }

    private static string ArtifactPlaceholder(ArtifactRefBlock artifact) =>
        string.Concat("[artifact ", artifact.ArtifactId, ": ", artifact.Description, "]");

    /// <summary>
    /// One summarizer request: the slot's system prompt, no tools, thinking off, output bounded by
    /// <paramref name="maxOutputTokens"/>. A failed request, a reply cut off at the output limit, a tool call or an empty
    /// reply is a <see cref="CompactionFailedException"/>; cancellation propagates as it is.
    /// </summary>
    private async Task<string> SummarizeAsync(AgentInstance instance, string userPrompt, int maxOutputTokens, CancellationToken ct)
    {
        var model = instance.Definition.Model;
        var settings = model with
        {
            Thinking = ThinkingEffort.Off,
            MaxOutputTokens = model.MaxOutputTokens is { } own && own > 0 ? Math.Min(own, maxOutputTokens) : maxOutputTokens,
        };
        var request = new ChatRequest(summarizerPrompt, [], [ChatMessage.User(userPrompt)], settings) { SessionId = instance.SessionId };
        var builder = new StringBuilder(2048);
        MessageCompleted? completed = null;
        var calledTool = false;
        try
        {
            var provider = providers(model);
            await foreach (var chunk in provider.StreamAsync(request, ct).ConfigureAwait(false))
            {
                switch (chunk)
                {
                    case TextDelta text:
                        builder.Append(text.Text);
                        break;
                    case ToolCallStarted:
                        calledTool = true;
                        break;
                    case MessageCompleted done:
                        completed = done;
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new CompactionFailedException("The summary request failed: " + exception.Message, exception);
        }

        if (completed is null)
        {
            throw new CompactionFailedException("The summary stream ended without a completed message.");
        }

        if (completed.Stop == StopReason.MaxTokens)
        {
            throw new CompactionFailedException("The summary was cut off at the output limit.");
        }

        if (calledTool || completed.Stop == StopReason.ToolUse || completed.Message.Content.Any(static block => block is ToolCallBlock))
        {
            throw new CompactionFailedException("The summarizer answered with a tool call instead of a summary.");
        }

        if (builder.Length == 0)
        {
            foreach (var block in completed.Message.Content)
            {
                if (block is TextBlock text)
                {
                    builder.Append(text.Text);
                }
            }
        }

        var summary = builder.ToString().Trim();
        return summary.Length > 0 ? summary : throw new CompactionFailedException("The summarizer returned an empty summary.");
    }
}
