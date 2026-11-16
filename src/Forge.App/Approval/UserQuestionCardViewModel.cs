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

/// <summary>One "why do you want to discuss" chip; at most one is on, and none is fine.</summary>
internal sealed class DiscussReasonViewModel : NotifyingObject
{
    private readonly QuestionItemViewModel owner;
    private readonly string labelKey;
    private bool isSelected;

    public DiscussReasonViewModel(QuestionItemViewModel owner, UserQuestionDiscussReason reason, string labelKey)
    {
        this.owner = owner;
        this.labelKey = labelKey;
        Reason = reason;
    }

    public UserQuestionDiscussReason Reason { get; }

    public string Label => Loc.Instance[labelKey];

    internal void RefreshLanguage() => OnPropertyChanged(nameof(Label));

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (Set(ref isSelected, value))
            {
                owner.OnReasonChanged(this);
            }
        }
    }

    internal void SetSilently(bool value)
    {
        if (isSelected != value)
        {
            isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }
}

/// <summary>
/// One question of a card: header chip, prompt, options (single or multi select), the "other, your own answer" row (selected
/// automatically once the user types) and the optional discussion request.
/// </summary>
internal sealed class QuestionItemViewModel : NotifyingObject
{
    private readonly Action changed;
    private readonly Action<QuestionItemViewModel> select;
    private string freeText = string.Empty;
    private string discussNote = string.Empty;
    private bool updating;
    private bool skipped;
    private bool otherSelected;
    private bool discussing;
    private bool isCurrent;
    private int ordinal;

    public QuestionItemViewModel(UserQuestion question, int ordinal, Action changed, Action<QuestionItemViewModel> select)
    {
        this.changed = changed;
        this.select = select;
        this.ordinal = ordinal;
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
        Reasons =
        [
            new(this, UserQuestionDiscussReason.Unclear, QuestionsStrings.ReasonUnclearKey),
            new(this, UserQuestionDiscussReason.Unrelated, QuestionsStrings.ReasonUnrelatedKey),
            new(this, UserQuestionDiscussReason.NeedsContext, QuestionsStrings.ReasonContextKey),
        ];
        SelectCommand = new RelayCommand(() => this.select(this));
    }

    public string Id { get; }

    public string Header { get; }

    public string Prompt { get; }

    public bool MultiSelect { get; }

    public bool IsSingleSelect => !MultiSelect;

    public bool AllowFreeText { get; }

    public IReadOnlyList<QuestionOptionViewModel> Options { get; }

    public IReadOnlyList<DiscussReasonViewModel> Reasons { get; }

    /// <summary>Makes this the visible question (the chips strip of a multi-question card).</summary>
    public RelayCommand SelectCommand { get; }

    /// <summary>Fewest picks that answer a multi-select question (free text counts as one); 1 for single-select.</summary>
    public int MinSelect { get; }

    /// <summary>Most picks allowed for a multi-select question (free text counts as one); 1 for single-select.</summary>
    public int MaxSelect { get; }

    /// <summary>Chip caption: the 1-based number, a check mark once resolved, then the header.</summary>
    public string ChipText => (IsResolved ? "✓ " : (ordinal + 1) + ". ") + Header;

    public bool IsCurrent
    {
        get => isCurrent;
        internal set => Set(ref isCurrent, value);
    }

    /// <summary>Picked options plus one when the "other" row is picked.</summary>
    public int PickCount => Options.Count(option => option.IsSelected) + (otherSelected ? 1 : 0);

    private bool HasFreeText => AllowFreeText && !string.IsNullOrWhiteSpace(freeText);

    /// <summary>Multi-select only: no further option may be picked.</summary>
    public bool IsAtMax => MultiSelect && PickCount >= MaxSelect;

    /// <summary>Localized selection-bound hint; empty for single-select.</summary>
    public string BoundsHint => !MultiSelect ? string.Empty : QuestionsStrings.BoundsHint(MinSelect, MaxSelect, Options.Count + (AllowFreeText ? 1 : 0));

    public bool IsEditable => !skipped;

    /// <summary>The "other" row is off while skipped, or when a multi-select question is full and the row is not picked.</summary>
    public bool IsFreeTextEnabled => !skipped && (!MultiSelect || otherSelected || PickCount < MaxSelect);

    /// <summary>The "other, your own answer" row is picked; typing into its box picks it automatically.</summary>
    public bool IsOtherSelected
    {
        get => otherSelected;
        set
        {
            if (value && !otherSelected && MultiSelect && IsAtMax)
            {
                OnPropertyChanged();
                return;
            }

            SetOther(value);
            if (otherSelected)
            {
                OnPicked();
            }

            RefreshState();
            changed();
        }
    }

    /// <summary>The user chose not to answer this question; it counts as resolved but not as answered.</summary>
    public bool IsSkipped
    {
        get => skipped;
        set
        {
            if (SetAndNotify(ref skipped, value, nameof(IsEditable)))
            {
                if (skipped)
                {
                    SetDiscussing(false);
                }

                RefreshState();
                changed();
            }
        }
    }

    /// <summary>The user wants to talk about this question before answering it; counts as resolved.</summary>
    public bool IsDiscussing
    {
        get => discussing;
        set
        {
            if (value == discussing)
            {
                return;
            }

            if (value)
            {
                ClearPicks();
                if (skipped)
                {
                    skipped = false;
                    OnPropertyChanged(nameof(IsSkipped));
                    OnPropertyChanged(nameof(IsEditable));
                }
            }

            SetDiscussing(value);
            RefreshState();
            changed();
        }
    }

    public string DiscussNote
    {
        get => discussNote;
        set => Set(ref discussNote, value ?? string.Empty);
    }

    /// <summary>The question is skipped, discussed, or its pick count is within bounds.</summary>
    public bool IsResolved => skipped || discussing || IsAnswered;

    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(BoundsHint));
        OnPropertyChanged(nameof(ChipText));
        foreach (var reason in Reasons)
        {
            reason.RefreshLanguage();
        }
    }

    internal void SetOrdinal(int value)
    {
        ordinal = value;
        OnPropertyChanged(nameof(ChipText));
    }

    private void RefreshOptions()
    {
        foreach (var option in Options)
        {
            option.RefreshEnabled();
        }
    }

    /// <summary>Typing picks the "other" row on its own (when it may still be picked) and cancels a discussion request.</summary>
    public string FreeText
    {
        get => freeText;
        set
        {
            if (!Set(ref freeText, value ?? string.Empty))
            {
                return;
            }

            if (HasFreeText && !otherSelected && !skipped && (!MultiSelect || !IsAtMax))
            {
                SetOther(true);
                OnPicked();
            }

            RefreshState();
            changed();
        }
    }

    /// <summary>Not skipped, not discussed, and the pick count is within [min, max] (the "other" row needs non-blank text).</summary>
    public bool IsAnswered =>
        !skipped && !discussing && (!otherSelected || HasFreeText)
        && (MultiSelect ? PickCount >= MinSelect && PickCount <= MaxSelect : PickCount >= 1);

    internal void OnOptionChanged(QuestionOptionViewModel changedOption)
    {
        if (updating)
        {
            return;
        }

        if (changedOption.IsSelected)
        {
            OnPicked();
            if (!MultiSelect)
            {
                // Single select: picking one clears the others (the "other" row too; its text stays).
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

                    SetOther(false);
                }
                finally
                {
                    updating = false;
                }
            }
        }

        RefreshState();
        changed();
    }

    internal void OnReasonChanged(DiscussReasonViewModel changedReason)
    {
        if (updating)
        {
            return;
        }

        if (changedReason.IsSelected)
        {
            foreach (var other in Reasons)
            {
                if (!ReferenceEquals(other, changedReason))
                {
                    other.SetSilently(false);
                }
            }
        }
    }

    /// <summary>A real pick (option or "other") was made: a pending discussion request is cancelled.</summary>
    private void OnPicked() => SetDiscussing(false);

    private void SetOther(bool value)
    {
        if (otherSelected == value)
        {
            return;
        }

        if (value && !MultiSelect)
        {
            updating = true;
            try
            {
                foreach (var option in Options)
                {
                    if (option.IsSelected)
                    {
                        option.IsSelected = false;
                    }
                }
            }
            finally
            {
                updating = false;
            }
        }

        otherSelected = value;
        OnPropertyChanged(nameof(IsOtherSelected));
    }

    private void SetDiscussing(bool value)
    {
        if (discussing == value)
        {
            return;
        }

        discussing = value;
        OnPropertyChanged(nameof(IsDiscussing));
    }

    private void ClearPicks()
    {
        updating = true;
        try
        {
            foreach (var option in Options)
            {
                if (option.IsSelected)
                {
                    option.IsSelected = false;
                }
            }
        }
        finally
        {
            updating = false;
        }

        if (otherSelected)
        {
            otherSelected = false;
            OnPropertyChanged(nameof(IsOtherSelected));
        }
    }

    private void RefreshState()
    {
        OnPropertyChanged(nameof(IsAnswered));
        OnPropertyChanged(nameof(IsResolved));
        OnPropertyChanged(nameof(ChipText));
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

        if (discussing)
        {
            var reason = Reasons.FirstOrDefault(item => item.IsSelected)?.Reason;
            var note = string.IsNullOrWhiteSpace(discussNote) ? null : discussNote.Trim();
            return new UserQuestionAnswer(Id, [], null, Discussion: new UserQuestionDiscussion(reason, note));
        }

        var selected = Options.Where(option => option.IsSelected).Select(option => option.Label).ToArray();
        var text = otherSelected && HasFreeText ? freeText.Trim() : null;
        return new UserQuestionAnswer(Id, selected, text);
    }
}

/// <summary>
/// An <c>ask_user</c> question card. It is shown as a tab of the workbench's bottom group (not in the approval overlay).
/// Submit completes it as <see cref="ApprovalCardDecision.Allow"/> with <see cref="Response"/> set; Dismiss completes it as a
/// denial; a withdrawal (turn cancelled, session released, shutdown) completes it as <see cref="ApprovalCardDecision.Withdrawn"/>,
/// which the handler reports as cancelled.
/// </summary>
internal sealed class UserQuestionCardViewModel : ApprovalCardViewModel
{
    private readonly string agentName;
    private UserQuestionResponse? response;
    private QuestionItemViewModel current;

    public UserQuestionCardViewModel(UserQuestionRequest request, string sessionTitle)
        : base("ask:" + Guid.NewGuid().ToString("N"), request.SessionId, sessionTitle, DateTimeOffset.UtcNow)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Questions.Count == 0)
        {
            throw new ArgumentException("A question card needs at least one question.", nameof(request));
        }

        agentName = string.IsNullOrWhiteSpace(request.AgentName) ? "main" : request.AgentName;
        Questions = [.. request.Questions.Select((question, index) => new QuestionItemViewModel(question, index, OnAnswerChanged, Select))];
        current = Questions[0];
        current.IsCurrent = true;
        SubmitCommand = new RelayCommand(Submit, () => CanSubmit);
        DismissCommand = new RelayCommand(Dismiss, () => !IsDecided);
        AttachLanguageChanged(OnLanguageChanged);
    }

    public IReadOnlyList<QuestionItemViewModel> Questions { get; }

    /// <summary>
    /// Raised by the panel view with the height its content needs (header, chips, current question, footer), so the bottom
    /// group can open tall enough to show every option without scrolling.
    /// </summary>
    public event Action<double>? ContentHeightReported;

    internal void ReportContentHeight(double height) => ContentHeightReported?.Invoke(height);

    /// <summary>The chips strip is only useful with more than one question.</summary>
    public bool HasMultipleQuestions => Questions.Count > 1;

    public RelayCommand SubmitCommand { get; }

    public RelayCommand DismissCommand { get; }

    public string AgentName => agentName;

    public string AskedByText => QuestionsStrings.AskedBy(agentName);

    public string Title => QuestionsStrings.CardTitle;

    /// <summary>The bottom-group tab title.</summary>
    public string TabTitle => QuestionsStrings.TabTitle(agentName);

    /// <summary>"Needs your input · the agent is waiting".</summary>
    public string HeaderText => QuestionsStrings.NeedsInput(agentName);

    private int waitingCount;

    /// <summary>Every question is resolved (answered, skipped or discussed), one is answered or discussed, and the card is open.</summary>
    public bool CanSubmit => !IsDecided
        && Questions.All(question => question.IsResolved)
        && Questions.Any(question => question.IsAnswered || question.IsDiscussing);

    /// <summary>At least one question asks for a discussion: submitting starts it instead of finishing the questions.</summary>
    public bool IsDiscussionRequested => Questions.Any(question => question.IsDiscussing);

    public string SubmitText => IsDiscussionRequested ? QuestionsStrings.SubmitDiscuss : QuestionsStrings.Submit;

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

    private void Select(QuestionItemViewModel question)
    {
        if (ReferenceEquals(current, question))
        {
            return;
        }

        current.IsCurrent = false;
        current = question;
        current.IsCurrent = true;
    }

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
        OnPropertyChanged(nameof(IsDiscussionRequested));
        OnPropertyChanged(nameof(SubmitText));
        OnPropertyChanged(nameof(AllSkipped));
        OnPropertyChanged(nameof(AllSkippedHint));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(AskedByText));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(SubmitText));
        OnPropertyChanged(nameof(WaitingText));
        OnPropertyChanged(nameof(AllSkippedHint));
        foreach (var question in Questions)
        {
            question.RefreshLanguage();
        }
    }
}
