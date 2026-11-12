using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Approval;

/// <summary>One option row of a question; the selection state is bound two-way to a radio button or a check box.</summary>
internal sealed class QuestionOptionViewModel : ObservableObject
{
    private readonly QuestionItemViewModel owner;
    private bool isSelected;

    public QuestionOptionViewModel(QuestionItemViewModel owner, UserQuestionOption option)
    {
        this.owner = owner;
        Label = option.Label;
        Description = option.Description ?? string.Empty;
    }

    public string Label { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    public bool IsMulti => owner.MultiSelect;

    public bool IsSingle => !owner.MultiSelect;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (Set(ref isSelected, value))
            {
                owner.OnOptionChanged(this);
            }
        }
    }
}

/// <summary>One question of a card: header chip, prompt, options (single or multi select) and the optional free text.</summary>
internal sealed class QuestionItemViewModel : ObservableObject
{
    private readonly Action changed;
    private string freeText = string.Empty;
    private bool updating;

    public QuestionItemViewModel(UserQuestion question, Action changed)
    {
        this.changed = changed;
        Id = question.Id;
        Header = question.Header;
        Prompt = question.Prompt;
        MultiSelect = question.MultiSelect;
        AllowFreeText = question.AllowFreeText;
        Options = [.. question.Options.Select(option => new QuestionOptionViewModel(this, option))];
    }

    public string Id { get; }

    public string Header { get; }

    public string Prompt { get; }

    public bool MultiSelect { get; }

    public bool IsSingleSelect => !MultiSelect;

    public bool AllowFreeText { get; }

    public IReadOnlyList<QuestionOptionViewModel> Options { get; }

    public string FreeText
    {
        get => freeText;
        set
        {
            if (Set(ref freeText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsAnswered));
                changed();
            }
        }
    }

    /// <summary>An option is picked, or (when allowed) some free text is typed.</summary>
    public bool IsAnswered =>
        Options.Any(option => option.IsSelected) || (AllowFreeText && !string.IsNullOrWhiteSpace(freeText));

    internal void OnOptionChanged(QuestionOptionViewModel changedOption)
    {
        if (updating)
        {
            return;
        }

        if (!MultiSelect && changedOption.IsSelected)
        {
            // Single select: picking one clears the others (a radio group does the same visually).
            updating = true;
            try
            {
                foreach (var other in Options)
                {
                    if (!ReferenceEquals(other, changedOption) && other.IsSelected)
                    {
                        other.IsSelected = false;
                    }
                }
            }
            finally
            {
                updating = false;
            }
        }

        OnPropertyChanged(nameof(IsAnswered));
        changed();
    }

    internal UserQuestionAnswer ToAnswer()
    {
        var selected = Options.Where(option => option.IsSelected).Select(option => option.Label).ToArray();
        var text = AllowFreeText && !string.IsNullOrWhiteSpace(freeText) ? freeText.Trim() : null;
        return new UserQuestionAnswer(Id, selected, text);
    }
}

/// <summary>
/// An <c>ask_user</c> question card in the approval queue. Submit completes it as <see cref="ApprovalCardDecision.Allow"/>
/// with <see cref="Response"/> set; Dismiss completes it as a denial; a withdrawal (turn cancelled, session released,
/// shutdown) completes it as <see cref="ApprovalCardDecision.Withdrawn"/>, which the handler reports as cancelled.
/// </summary>
internal sealed class UserQuestionCardViewModel : ApprovalCardViewModel
{
    private readonly string agentName;
    private UserQuestionResponse? response;

    public UserQuestionCardViewModel(UserQuestionRequest request, string sessionTitle)
        : base("ask:" + Guid.NewGuid().ToString("N"), request.SessionId, sessionTitle, DateTimeOffset.UtcNow)
    {
        ArgumentNullException.ThrowIfNull(request);
        agentName = string.IsNullOrWhiteSpace(request.AgentName) ? "main" : request.AgentName;
        Questions = [.. request.Questions.Select(question => new QuestionItemViewModel(question, OnAnswerChanged))];
        SubmitCommand = new RelayCommand(Submit, () => CanSubmit);
        DismissCommand = new RelayCommand(Dismiss, () => !IsDecided);
        AttachLanguageChanged(OnLanguageChanged);
    }

    public IReadOnlyList<QuestionItemViewModel> Questions { get; }

    public RelayCommand SubmitCommand { get; }

    public RelayCommand DismissCommand { get; }

    public string AgentName => agentName;

    public string AskedByText => QuestionsStrings.AskedBy(agentName);

    public string Title => QuestionsStrings.CardTitle;

    /// <summary>Every question has an answer and the card is still open.</summary>
    public bool CanSubmit => !IsDecided && Questions.All(question => question.IsAnswered);

    /// <summary>The answers once the user submitted; null before.</summary>
    public UserQuestionResponse? Response => response;

    /// <summary>A withdrawal is "cancelled", not a refusal: the waiter must tell the model which one it was.</summary>
    protected override ApprovalCardOutcome WithdrawalOutcome => new(ApprovalCardDecision.Withdrawn);

    private void Submit()
    {
        if (!CanSubmit)
        {
            return;
        }

        response = new UserQuestionResponse(UserQuestionOutcome.Answered, [.. Questions.Select(question => question.ToAnswer())]);
        if (!TryComplete(ApprovalCardOutcome.Allow))
        {
            response = null;
        }
    }

    private void Dismiss() => TryComplete(ApprovalCardOutcome.Deny);

    private void OnAnswerChanged() => SubmitCommand.NotifyCanExecuteChanged();

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AskedByText));
        OnPropertyChanged(nameof(Title));
    }
}
