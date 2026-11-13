namespace Forge.Abstractions;

/// <summary>One choice of a <see cref="UserQuestion"/>.</summary>
public sealed record UserQuestionOption(string Label, string? Description = null);

/// <summary>
/// One question the agent puts to the user. <paramref name="Id"/> is unique within its request and comes back in the answer;
/// <paramref name="Header"/> is a short chip label (at most 12 characters), <paramref name="Prompt"/> the full question.
/// Two to six <paramref name="Options"/>; <paramref name="AllowFreeText"/> adds an "other" text box.
/// For a <paramref name="MultiSelect"/> question <paramref name="MinSelect"/>/<paramref name="MaxSelect"/> bound how many
/// options may be picked (null: at least one, at most all); free text counts as one pick. A single-select question ignores
/// both. Every question may be skipped by the user (see <see cref="UserQuestionAnswer.Skipped"/>).
/// </summary>
public sealed record UserQuestion(
    string Id,
    string Header,
    string Prompt,
    IReadOnlyList<UserQuestionOption> Options,
    bool MultiSelect = false,
    bool AllowFreeText = true,
    int? MinSelect = null,
    int? MaxSelect = null);

/// <summary>
/// A batch of one to four questions from one agent. <paramref name="AgentName"/> is shown on the card so the user knows
/// whether the main agent or a sub-agent is asking.
/// </summary>
public sealed record UserQuestionRequest(
    string SessionId,
    string? AgentId,
    string? AgentName,
    IReadOnlyList<UserQuestion> Questions);

/// <summary>
/// The answer to one question: the labels picked (in option order) and the free text, if any. <paramref name="Skipped"/>
/// means the user chose not to answer this question while answering others; Selected is then empty and FreeText null.
/// </summary>
public sealed record UserQuestionAnswer(string QuestionId, IReadOnlyList<string> Selected, string? FreeText = null, bool Skipped = false);

public enum UserQuestionOutcome
{
    /// <summary>The user answered; <see cref="UserQuestionResponse.Answers"/> has one entry per question.</summary>
    Answered,

    /// <summary>The user dismissed the card without answering; the agent should continue on its own judgment.</summary>
    Declined,

    /// <summary>The turn or the session ended while the card was open.</summary>
    Cancelled,
}

public sealed record UserQuestionResponse(UserQuestionOutcome Outcome, IReadOnlyList<UserQuestionAnswer> Answers)
{
    public static UserQuestionResponse Declined { get; } = new(UserQuestionOutcome.Declined, []);

    public static UserQuestionResponse Cancelled { get; } = new(UserQuestionOutcome.Cancelled, []);
}

/// <summary>
/// Interactive side of the <c>ask_user</c> tool, implemented by the UI layer. The call completes when the user answers or
/// dismisses the card; cancelling <paramref name="ct"/> withdraws the card and yields <see cref="UserQuestionOutcome.Cancelled"/>
/// (it does not throw for a cancelled turn, so the tool can report the outcome).
/// </summary>
public interface IUserQuestionHandler
{
    ValueTask<UserQuestionResponse> AskAsync(UserQuestionRequest request, CancellationToken ct);
}
