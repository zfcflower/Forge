using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Approval;

/// <summary>One option row of a question; the selection state is bound two-way to a radio button or a check box.</summary>
internal sealed class QuestionOptionViewModel : NotifyingObject
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

    /// <summary>False when the question is skipped, or (multi-select) the max is reached and this option is not picked.</summary>
    public bool IsEnabled => !owner.IsSkipped && (!owner.MultiSelect || isSelected || !owner.IsAtMax);

    internal void RefreshEnabled() => OnPropertyChanged(nameof(IsEnabled));

    public bool IsMulti => owner.MultiSelect;

    public bool IsSingle => !owner.MultiSelect;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (value && !isSelected && owner.MultiSelect && owner.IsAtMax)
            {
                // The max is reached: a further pick is refused (the check box snaps back).
                OnPropertyChanged();
                return;
            }

            if (SetAndNotify(ref isSelected, value, nameof(IsEnabled)))
            {
                owner.OnOptionChanged(this);
            }
        }
    }
}

/// <summary>One question of a card: header chip, prompt, options (single or multi select) and the optional free text.</summary>
internal sealed class QuestionItemViewModel : NotifyingObject
{
    private readonly Action changed;
    private string freeText = string.Empty;
    private bool updating;
    private bool skipped;

    public QuestionItemViewModel(UserQuestion question, Action changed)
    {
        this.changed = changed;
        Id = question.Id;
        Header = question.Header;
        Prompt = question.Prompt;
        MultiSelect = question.MultiSelect;
        AllowFreeText = question.AllowFreeText;
        var capacity = question.Options.Count + (question.AllowFreeText ? 1 : 0);
        if (question.MultiSelect)
        {
            MinSelect = Math.Clamp(question.MinSelect ?? 1, 1, capacity);
            MaxSelect = Math.Clamp(question.MaxSelect ?? capacity, MinSelect, capacity);
        }
        else
        {
            MinSelect = 1;
            MaxSelect = 1;
        }

        Options = [.. question.Options.Select(option => new QuestionOptionViewModel(this, option))];
    }

    public string Id { get; }

    public string Header { get; }

    public string Prompt { get; }

    public bool MultiSelect { get; }

    public bool IsSingleSelect => !MultiSelect;

    public bool AllowFreeText { get; }

    public IReadOnlyList<QuestionOptionViewModel> Options { get; }

    /// <summary>Fewest picks that answer a multi-select question (free text counts as one); 1 for single-select.</summary>
    public int MinSelect { get; }

    /// <summary>Most picks allowed for a multi-select question (free text counts as one); 1 for single-select.</summary>
    public int MaxSelect { get; }

    /// <summary>Picked options plus one when free text is typed.</summary>
    public int PickCount => Options.Count(option => option.IsSelected) + (HasFreeText ? 1 : 0);

    private bool HasFreeText => AllowFreeText && !string.IsNullOrWhiteSpace(freeText);

    /// <summary>Multi-select only: no further option may be picked.</summary>
    public bool IsAtMax => MultiSelect && PickCount >= MaxSelect;

    /// <summary>Localized selection-bound hint; empty for single-select.</summary>
    public string BoundsHint => !MultiSelect ? string.Empty : QuestionsStrings.BoundsHint(MinSelect, MaxSelect, Options.Count + (AllowFreeText ? 1 : 0));

    public bool IsEditable => !skipped;

    /// <summary>The free-text box is off while skipped, or when a multi-select question is full and the box is empty.</summary>
    public bool IsFreeTextEnabled => !skipped && (!MultiSelect || HasFreeText || PickCount < MaxSelect);

    /// <summary>The user chose not to answer this question; it counts as resolved but not as answered.</summary>
    public bool IsSkipped
    {
        get => skipped;
        set
        {
            if (SetAndNotify(ref skipped, value, nameof(IsEditable)))
            {
                OnPropertyChanged(nameof(IsFreeTextEnabled));
                OnPropertyChanged(nameof(IsAnswered));
                RefreshOptions();
                changed();
            }
        }
    }

    /// <summary>The question is skipped, or its pick count is within bounds.</summary>
    public bool IsResolved => skipped || IsAnswered;

    internal void RefreshLanguage() => OnPropertyChanged(nameof(BoundsHint));

    private void RefreshOptions()
    {
        foreach (var option in Options)
        {
            option.RefreshEnabled();
        }
    }

    public string FreeText
    {
        get => freeText;
        set
        {
            if (Set(ref freeText, value ?? string.Empty))
            {
                RefreshState();
                changed();
            }
        }
    }

    /// <summary>Not skipped and the pick count (options plus non-blank free text) is within [min, max].</summary>
    public bool IsAnswered => !skipped && (MultiSelect ? PickCount >= MinSelect && PickCount <= MaxSelect : PickCount >= 1);

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

        RefreshState();
        changed();
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(IsAnswered));
        OnPropertyChanged(nameof(IsResolved));
        OnPropertyChanged(nameof(PickCount));
        OnPropertyChanged(nameof(IsAtMax));
        OnPropertyChanged(nameof(IsFreeTextEnabled));
        RefreshOptions();
    }

    internal UserQuestionAnswer ToAnswer()
    {
        if (skipped)
        {
            return new UserQuestionAnswer(Id, [], null, Skipped: true);
        }

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

    private int waitingCount;

    /// <summary>Every question is answered or skipped, at least one is answered, and the card is still open.</summary>
    public bool CanSubmit => !IsDecided && Questions.All(question => question.IsResolved) && Questions.Any(question => question.IsAnswered);

    /// <summary>True when every question is skipped: nothing to submit, the user should dismiss instead.</summary>
    public bool AllSkipped => Questions.All(question => question.IsSkipped);

    /// <summary>Shown when the submit is disabled because everything was skipped.</summary>
    public string AllSkippedHint => AllSkipped ? QuestionsStrings.AllSkippedHint : string.Empty;

    /// <summary>Other question cards of the same session waiting behind this one; set by the queue on the UI thread.</summary>
    public int WaitingCount
    {
        get => waitingCount;
        internal set
        {
            if (SetAndNotify(ref waitingCount, value, nameof(HasWaiting)))
            {
                OnPropertyChanged(nameof(WaitingText));
            }
        }
    }

    public bool HasWaiting => waitingCount > 0;

    public string WaitingText => waitingCount > 0 ? QuestionsStrings.Waiting(waitingCount) : string.Empty;

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

    private void OnAnswerChanged()
    {
        SubmitCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSubmit));
        OnPropertyChanged(nameof(AllSkipped));
        OnPropertyChanged(nameof(AllSkippedHint));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AskedByText));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(WaitingText));
        OnPropertyChanged(nameof(AllSkippedHint));
        foreach (var question in Questions)
        {
            question.RefreshLanguage();
        }
    }
}
