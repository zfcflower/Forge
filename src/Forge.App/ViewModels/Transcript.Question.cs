using System.Text.Json;
using Forge.App.Localization;

namespace Forge.App.ViewModels;

/// <summary>One option of an <c>ask_user</c> question: what it says and whether the user picked it.</summary>
internal sealed record QuestionOptionItem(string Label, string Description, bool IsChosen)
{
    public bool HasDescription => Description.Length > 0;
}

/// <summary>One question of an <c>ask_user</c> call as the transcript shows it: header, prompt, the options and the answer.</summary>
internal sealed record QuestionCardItem(string Header, string Prompt, string Answer, IReadOnlyList<QuestionOptionItem> Options)
{
    public bool HasAnswer => Answer.Length > 0;

    public bool HasOptions => Options.Count > 0;
}

// The Q&A card of an ask_user call. It is stateless: everything comes from the call's arguments and result text, so it looks
// the same live and when a stored transcript is replayed.
internal sealed partial class ToolRowViewModel
{
    private const string SkippedMarker = "(skipped)";
    private const string NoAnswerMarker = "(no answer)";
    private const string DiscussionMarker = "(discussion requested";
    private const string DismissedPrefix = "The user dismissed";

    private IReadOnlyList<QuestionCardItem>? questionItems;

    /// <summary>True for an <c>ask_user</c> call: the row draws as a question-and-answer card instead of a plain tool row.</summary>
    /// <summary>A failed call (invalid arguments, handler error) keeps the plain tool row so its error text stays visible.</summary>
    public bool IsQuestionCard => IsAskUser && !IsError;

    /// <summary>The row itself for an <c>ask_user</c> card, null for every other row: the slot the card's controls are built in, so a tool row does not build them hidden.</summary>
    public object? QuestionBody => IsQuestionCard ? this : null;

    private bool IsAskUser => string.Equals(Name, Forge.Core.Agents.AskUserTool.Name, StringComparison.Ordinal);

    public IReadOnlyList<QuestionCardItem> QuestionItems => questionItems ??= BuildQuestionItems();

    /// <summary>Waiting / answered / dismissed / cancelled / discussing, in the current language.</summary>
    public string QuestionStatusText
    {
        get
        {
            if (!HasResult)
            {
                return Loc.Instance[QuestionsStrings.TranscriptPendingKey];
            }

            var text = Result ?? string.Empty;
            if (string.Equals(text, "cancelled", StringComparison.Ordinal))
            {
                return Loc.Instance[QuestionsStrings.TranscriptCancelledKey];
            }

            if (text.StartsWith(DismissedPrefix, StringComparison.Ordinal))
            {
                return Loc.Instance[QuestionsStrings.TranscriptDismissedKey];
            }

            return text.Contains(DiscussionMarker, StringComparison.Ordinal)
                ? Loc.Instance[QuestionsStrings.TranscriptDiscussingKey]
                : Loc.Instance[QuestionsStrings.TranscriptAnsweredKey];
        }
    }

    private void RaiseQuestionCard()
    {
        if (!IsAskUser)
        {
            return;
        }

        questionItems = null;
        OnPropertyChanged(nameof(IsQuestionCard));
        OnPropertyChanged(nameof(QuestionBody));
        OnPropertyChanged(nameof(QuestionItems));
        OnPropertyChanged(nameof(QuestionStatusText));
        OnPropertyChanged(nameof(CardBody));
    }

    /// <summary>Turns the model-facing wording of one answer into the user's language.</summary>
    private static string Describe(string raw)
    {
        if (string.Equals(raw, SkippedMarker, StringComparison.Ordinal))
        {
            return Loc.Instance[QuestionsStrings.TranscriptSkippedKey];
        }

        if (string.Equals(raw, NoAnswerMarker, StringComparison.Ordinal))
        {
            return Loc.Instance[QuestionsStrings.TranscriptNoAnswerKey];
        }

        if (raw.StartsWith(DiscussionMarker, StringComparison.Ordinal))
        {
            var close = raw.IndexOf(')', StringComparison.Ordinal);
            var note = close >= 0 ? raw[(close + 1)..].Trim() : string.Empty;
            var label = Loc.Instance[QuestionsStrings.TranscriptDiscussingKey];
            return note.Length > 0 ? label + ": " + note : label;
        }

        // "other: text" is the model-facing tag of a typed answer, possibly after chosen options ("A; other: text").
        const string OtherTag = "other: ";
        if (raw.StartsWith(OtherTag, StringComparison.Ordinal))
        {
            return raw[OtherTag.Length..];
        }

        return raw.Replace("; " + OtherTag, "; ", StringComparison.Ordinal);
    }

    private List<QuestionCardItem> BuildQuestionItems()
    {
        var items = new List<QuestionCardItem>();
        if (!IsQuestionCard)
        {
            return items;
        }

        var lines = (Result ?? string.Empty).Split('\n');
        var cursor = 0;
        var answered = HasResult
            && !string.Equals(Result, "cancelled", StringComparison.Ordinal)
            && Result?.StartsWith(DismissedPrefix, StringComparison.Ordinal) != true;
        try
        {
            using var document = JsonDocument.Parse(Arguments);
            if (!document.RootElement.TryGetProperty("questions", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return items;
            }

            foreach (var element in array.EnumerateArray())
            {
                var header = element.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? string.Empty : string.Empty;
                var prompt = element.TryGetProperty("question", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() ?? string.Empty : string.Empty;
                var answer = string.Empty;
                if (answered)
                {
                    // The tool writes one "header: answer" line per question, in order and with the trimmed header; walking
                    // forward keeps two questions with the same header apart (a free-text answer may add lines of its own).
                    var prefix = header.Trim() + ": ";
                    for (; cursor < lines.Length; cursor++)
                    {
                        if (lines[cursor].StartsWith(prefix, StringComparison.Ordinal))
                        {
                            answer = Describe(lines[cursor][prefix.Length..].TrimEnd('\r'));
                            cursor++;
                            break;
                        }
                    }
                }

                items.Add(new QuestionCardItem(header, prompt, answer, OptionsOf(element, answer)));
            }
        }
        catch (JsonException)
        {
            // Malformed arguments (the call failed validation): the card then shows no items and only the status.
        }

        return items;
    }

    /// <summary>
    /// The options the call offered, each marked with whether the answer picked it. The model-facing answer lists the chosen
    /// labels separated by "; " (free text rides along as "other: …"), so a segment that equals a label is a pick.
    /// </summary>
    private static IReadOnlyList<QuestionOptionItem> OptionsOf(JsonElement question, string answer)
    {
        if (!question.TryGetProperty("options", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var chosen = answer.Split("; ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var options = new List<QuestionOptionItem>(array.GetArrayLength());
        foreach (var element in array.EnumerateArray())
        {
            var label = element.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() ?? string.Empty : string.Empty;
            if (label.Length == 0)
            {
                continue;
            }

            var description = element.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? string.Empty : string.Empty;
            options.Add(new QuestionOptionItem(label, description, Array.IndexOf(chosen, label) >= 0));
        }

        return options;
    }
}
