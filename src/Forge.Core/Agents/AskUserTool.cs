using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// Lets an agent put one to four structured questions to the user and continue with the answers. It is read-only (it
/// changes nothing and is itself the user interaction, so it never goes through approval); it is registered only when the
/// host has an <see cref="IUserQuestionHandler"/>. Cancelling the turn yields a "cancelled" result, never a hang.
/// </summary>
public sealed class AskUserTool : ITool
{
    public const string Name = "ask_user";

    public const int MaxQuestions = 4;
    public const int MinOptions = 2;
    public const int MaxOptions = 6;
    public const int MaxHeaderLength = 12;

    private const string SchemaJson =
        """{"type":"object","properties":{"questions":{"type":"array","minItems":1,"maxItems":4,"description":"One to four questions asked together.","items":{"type":"object","properties":{"id":{"type":"string","description":"Optional stable id, unique within the call; q1..qN are generated when absent."},"header":{"type":"string","maxLength":12,"description":"Very short label shown as a chip (at most 12 characters)."},"question":{"type":"string","description":"The full question, ending with a question mark."},"options":{"type":"array","minItems":2,"maxItems":6,"description":"The choices; put the one you recommend first.","items":{"type":"object","properties":{"label":{"type":"string","description":"Short choice text, unique within the question."},"description":{"type":"string","description":"What picking it means or implies."}},"required":["label"],"additionalProperties":false}},"multiSelect":{"type":"boolean","description":"Allow picking several options. Default false."},"allowFreeText":{"type":"boolean","description":"Offer an 'other' text box next to the options. Default true."}},"required":["header","question","options"],"additionalProperties":false}}},"required":["questions"],"additionalProperties":false}""";

    private const string Description =
        "Ask the user one to four multiple-choice questions and wait for the answers. Use it only for genuine decisions that only the user can make and that you cannot settle by reading the code or the project: a preference between valid designs, a missing requirement, a choice with different costs for them. "
        + "Do not use it to confirm obvious next steps, to ask permission for work you were already asked to do, or for anything you can decide or look up yourself; prefer a sensible default and say so. "
        + "Batch related questions into one call instead of asking one by one. Give each question a short header (at most 12 characters) and two to six distinct options with a one-line description each; list your recommended option first. Set multiSelect when several options may apply; the user can also type a free-text answer unless allowFreeText is false. "
        + "The result lists each question's chosen labels and any free text. If the user dismisses the questions, continue on your best judgment or ask in plain text.";

    private readonly IUserQuestionHandler handler;

    public AskUserTool(IUserQuestionHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        this.handler = handler;
        Definition = new ToolDefinition(Name, Description, SchemaJson);
    }

    public ToolDefinition Definition { get; }

    public ToolSideEffect SideEffect => ToolSideEffect.ReadOnly;

    public async ValueTask<ToolResult> InvokeAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(context);
        var problem = Parse(call.ArgumentsJson, out var questions);
        if (problem is not null)
        {
            return new ToolResult($"{Name}: {problem}", IsError: true);
        }

        if (ct.IsCancellationRequested)
        {
            return new ToolResult("cancelled");
        }

        UserQuestionResponse response;
        try
        {
            response = await handler
                .AskAsync(new UserQuestionRequest(context.SessionId, context.AgentId, context.AgentName, questions), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ToolResult("cancelled");
        }
        catch (Exception exception)
        {
            return new ToolResult($"{Name} failed: {exception.Message}", IsError: true);
        }

        return Format(response, questions);
    }

    /// <summary>Validates the arguments; returns the problem text, or null with the questions filled in.</summary>
    internal static string? Parse(string argumentsJson, out IReadOnlyList<UserQuestion> questions)
    {
        questions = [];
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "arguments must be a JSON object.";
            }

            if (!root.TryGetProperty("questions", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return "requires a 'questions' array.";
            }

            var count = array.GetArrayLength();
            if (count is < 1 or > MaxQuestions)
            {
                return $"'questions' must hold 1 to {MaxQuestions} items, got {count}.";
            }

            var list = new List<UserQuestion>(count);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in array.EnumerateArray())
            {
                index++;
                var problem = ParseQuestion(item, index, out var question);
                if (problem is not null)
                {
                    return problem;
                }

                if (!ids.Add(question!.Id))
                {
                    return $"question {index}: duplicate id '{question.Id}'.";
                }

                list.Add(question);
            }

            questions = list;
            return null;
        }
        catch (JsonException exception)
        {
            return $"arguments are invalid: {exception.Message}";
        }
    }

    private static string? ParseQuestion(JsonElement item, int index, out UserQuestion? question)
    {
        question = null;
        var at = $"question {index}: ";
        if (item.ValueKind != JsonValueKind.Object)
        {
            return at + "must be an object.";
        }

        var id = JsonArguments.OptionalString(item, "id")?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            id = "q" + index.ToString(CultureInfo.InvariantCulture);
        }

        var header = JsonArguments.OptionalString(item, "header")?.Trim();
        if (string.IsNullOrEmpty(header))
        {
            return at + "requires a non-empty 'header'.";
        }

        if (header.Length > MaxHeaderLength)
        {
            return at + $"'header' must be at most {MaxHeaderLength} characters, got {header.Length}.";
        }

        var prompt = JsonArguments.OptionalString(item, "question")?.Trim();
        if (string.IsNullOrEmpty(prompt))
        {
            return at + "requires a non-empty 'question'.";
        }

        if (!item.TryGetProperty("options", out var optionsElement) || optionsElement.ValueKind != JsonValueKind.Array)
        {
            return at + "requires an 'options' array.";
        }

        var optionCount = optionsElement.GetArrayLength();
        if (optionCount is < MinOptions or > MaxOptions)
        {
            return at + $"'options' must hold {MinOptions} to {MaxOptions} items, got {optionCount}.";
        }

        var options = new List<UserQuestionOption>(optionCount);
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in optionsElement.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
            {
                return at + "each option must be an object with a 'label'.";
            }

            var label = JsonArguments.OptionalString(option, "label")?.Trim();
            if (string.IsNullOrEmpty(label))
            {
                return at + "each option requires a non-empty 'label'.";
            }

            if (!labels.Add(label))
            {
                return at + $"duplicate option label '{label}'.";
            }

            var description = JsonArguments.OptionalString(option, "description")?.Trim();
            options.Add(new UserQuestionOption(label, string.IsNullOrEmpty(description) ? null : description));
        }

        question = new UserQuestion(
            id,
            header,
            prompt,
            options,
            OptionalBool(item, "multiSelect", false),
            OptionalBool(item, "allowFreeText", true));
        return null;
    }

    private static bool OptionalBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var element) && (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
            ? element.GetBoolean()
            : fallback;

    private static ToolResult Format(UserQuestionResponse response, IReadOnlyList<UserQuestion> questions)
    {
        switch (response.Outcome)
        {
            case UserQuestionOutcome.Cancelled:
                return new ToolResult("cancelled");
            case UserQuestionOutcome.Declined:
                return new ToolResult(
                    "The user dismissed the questions without answering. Proceed on your best judgment, or ask in plain text if you cannot continue without an answer.");
        }

        var builder = new StringBuilder();
        foreach (var question in questions)
        {
            UserQuestionAnswer? answer = null;
            foreach (var candidate in response.Answers)
            {
                if (string.Equals(candidate.QuestionId, question.Id, StringComparison.Ordinal))
                {
                    answer = candidate;
                    break;
                }
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(question.Header).Append(": ");
            var wrote = false;
            if (answer is not null && answer.Selected.Count > 0)
            {
                builder.AppendJoin(", ", answer.Selected);
                wrote = true;
            }

            if (answer is not null && !string.IsNullOrWhiteSpace(answer.FreeText))
            {
                if (wrote)
                {
                    builder.Append("; ");
                }

                builder.Append("other: ").Append(answer.FreeText.Trim());
                wrote = true;
            }

            if (!wrote)
            {
                builder.Append("(no answer)");
            }
        }

        return new ToolResult(builder.ToString());
    }
}
