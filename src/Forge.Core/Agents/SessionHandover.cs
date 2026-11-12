using System.Text;
using Forge.Abstractions;
using Forge.Core.Storage;

namespace Forge.Core.Agents;

/// <summary>The handover summary could not be produced: the provider failed or answered with nothing.</summary>
public sealed class HandoverSummaryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Handover summary (context mode "summary", section 8.1): one model call that turns the old session's context and a
/// description of its uncommitted changes into the text of the first, persisted user message of the new session.
/// It has its own prompt; the compactor's <see cref="SummaryCompactor.SummarizerPrompt"/> serves a different reader.
/// </summary>
public static class HandoverSummary
{
    public const string Prompt =
        """
        You write the handover note that lets a new session of a coding agent continue the work of a previous session. The new session sees only this note, not the previous conversation.

        Answer with exactly these Markdown sections, in this order:
        ## Goal
        The task and its constraints as the user stated them.
        ## Done
        What was completed: files and symbols touched, decisions taken and why, facts established by tool output.
        ## Uncommitted changes
        What the working-tree description reports and what those changes are for; "None." when it reports none.
        ## Open items
        Unresolved questions, failing or unrun checks, known risks.
        ## Next steps
        The concrete actions to take first, in order.

        Use only facts from the conversation and the working-tree description. No preamble, under 500 words.
        """;

    /// <summary>Opening line of the persisted message, so the model can tell the note from a user request.</summary>
    public const string MessageHeading = "Handover summary from the previous session (its conversation is not in this context):";

    private const int MaxMessageChars = 6_000;
    private const int MaxTranscriptChars = 400_000;

    /// <summary>
    /// Generates the summary with <paramref name="provider"/> / <paramref name="model"/> (thinking off) and returns the
    /// complete text of the user message to persist. Cancellation surfaces as <see cref="OperationCanceledException"/>;
    /// every other failure, and an empty answer, as <see cref="HandoverSummaryException"/>.
    /// </summary>
    /// <param name="history">The old session's context, oldest first; when it is too long the most recent part is kept.</param>
    /// <param name="uncommittedChanges">Description of the working tree (for example <c>git status</c> / a diff stat); null or empty = none.</param>
    /// <param name="prompt">The effective <c>handover.summary</c> slot; null or blank = <see cref="Prompt"/>.</param>
    public static async Task<string> GenerateAsync(
        IChatProvider provider,
        ModelSettings model,
        IReadOnlyList<ChatMessage> history,
        string? uncommittedChanges,
        CancellationToken ct,
        string? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(history);
        ct.ThrowIfCancellationRequested();

        var request = new ChatRequest(
            string.IsNullOrWhiteSpace(prompt) ? Prompt : prompt,
            [],
            [ChatMessage.User(RenderInput(history, uncommittedChanges))],
            model with { Thinking = ThinkingEffort.Off });
        var builder = new StringBuilder(2048);
        try
        {
            await foreach (var chunk in provider.StreamAsync(request, ct).ConfigureAwait(false))
            {
                switch (chunk)
                {
                    case TextDelta text:
                        builder.Append(text.Text);
                        break;
                    case MessageCompleted completed when builder.Length == 0:
                        foreach (var block in completed.Message.Content)
                        {
                            if (block is TextBlock text)
                            {
                                builder.Append(text.Text);
                            }
                        }

                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new HandoverSummaryException("The handover summary could not be generated: " + exception.Message, exception);
        }

        var summary = builder.ToString().Trim();
        if (summary.Length == 0)
        {
            throw new HandoverSummaryException("The handover summary could not be generated: the model returned no text.");
        }

        return string.Concat(MessageHeading, "\n\n", summary);
    }

    internal static string RenderInput(IReadOnlyList<ChatMessage> history, string? uncommittedChanges)
    {
        var lines = new List<string>(history.Count);
        var total = 0;
        var omitted = false;
        for (var index = history.Count - 1; index >= 0; index--)
        {
            var message = history[index];
            var text = MessageJson.ExtractFullText(message);
            if (text.Length > MaxMessageChars)
            {
                text = string.Concat(text.AsSpan(0, MaxMessageChars), "…");
            }

            var line = string.Concat(MessageJson.RoleName(message.Role), ": ", text);
            if (total + line.Length > MaxTranscriptChars && lines.Count > 0)
            {
                omitted = true;
                break;
            }

            lines.Add(line);
            total += line.Length + 1;
        }

        lines.Reverse();
        var builder = new StringBuilder(total + 256);
        builder.Append("# Conversation\n\n");
        if (omitted)
        {
            builder.Append("[earlier messages omitted for length]\n");
        }

        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }

        builder.Append("\n# Working tree\n\n");
        builder.Append(string.IsNullOrWhiteSpace(uncommittedChanges) ? "No uncommitted changes." : uncommittedChanges.Trim());
        return builder.ToString();
    }
}

/// <summary>
/// The inheritance note (section 8.2): a persisted user message appended right after an inherited or summarized
/// context, telling the model where the repository stands now.
/// </summary>
public static class InheritanceNote
{
    /// <param name="headSha">Commit the new session's working tree is at.</param>
    /// <param name="handoverCommitSha">Commit made for the handover, if one was made; null = none.</param>
    /// <param name="uncommittedChangesCarried">True when the old working tree's uncommitted changes came along.</param>
    public static string Build(string headSha, string? handoverCommitSha, bool uncommittedChangesCarried)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headSha);
        var builder = new StringBuilder(512);
        builder.Append("[Session handover] This session continues the work of a previous session; the context above comes from it.\n");
        builder.Append("- Current HEAD: ").Append(headSha.Trim()).Append('\n');
        builder.Append("- Handover commit: ")
            .Append(string.IsNullOrWhiteSpace(handoverCommitSha) ? "none" : handoverCommitSha.Trim())
            .Append('\n');
        builder.Append("- Uncommitted changes: ")
            .Append(uncommittedChangesCarried
                ? "carried over into this working tree"
                : "not carried over")
            .Append('\n');
        builder.Append("\nFiles read earlier in this history may have changed since. Re-read a file before editing it.");
        return builder.ToString();
    }

    public static ChatMessage Message(string headSha, string? handoverCommitSha, bool uncommittedChangesCarried) =>
        ChatMessage.User(Build(headSha, handoverCommitSha, uncommittedChangesCarried));

    /// <summary>
    /// The note a full-inheritance parallel fork starts with: the history above is the source session's, which goes on
    /// separately; this session works in its own worktree on <paramref name="forkBranch"/>, started at
    /// <paramref name="startSha"/>, and the source's uncommitted changes did not come along.
    /// </summary>
    public static string BuildFork(string sourceBranch, string forkBranch, string startSha, string? handoverCommitSha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(forkBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(startSha);
        var builder = new StringBuilder(512);
        builder.Append("[Parallel fork] This session is a parallel fork of the session whose context is above. That session goes on separately on its own branch.\n");
        builder.Append("- Forked from branch: ").Append(sourceBranch.Trim()).Append('\n');
        builder.Append("- This session's branch: ").Append(forkBranch.Trim()).Append(" (its own worktree)\n");
        builder.Append("- Start commit: ").Append(startSha.Trim()).Append('\n');
        builder.Append("- Commit made before forking: ")
            .Append(string.IsNullOrWhiteSpace(handoverCommitSha) ? "none" : handoverCommitSha.Trim())
            .Append('\n');
        builder.Append("- Uncommitted changes of the source: not carried over\n");
        builder.Append("\nThe working directory differs from the one in the history above, and files may differ too. Re-read a file before editing it.");
        return builder.ToString();
    }
}
