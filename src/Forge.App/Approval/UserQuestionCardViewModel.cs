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

    /// <summary>False when (multi-select) the max is reached and this option is not picked.</summary>
    public bool IsEnabled => !owner.MultiSelect || isSelected || !owner.IsAtMax;

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
        ToggleDiscussCommand = new RelayCommand(() => IsDiscussing = !IsDiscussing);
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

    /// <summary>Opens or closes the inline discussion request from the question's header button.</summary>
    public RelayCommand ToggleDiscussCommand { get; }

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

    /// <summary>The "other" row is off while a multi-select question is full and the row is not picked.</summary>
    public bool IsFreeTextEnabled => !MultiSelect || otherSelected || PickCount < MaxSelect;

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
            }

            SetDiscussing(value);
            RefreshState();
            changed();
        }
    }

    /// <summary>
    /// The request the agent will read: what the user would like explained. A reason or a note is required — an empty
    /// request would send the agent off to discuss nothing.
    /// </summary>
    public string DiscussNote
    {
        get => discussNote;
        set
        {
            if (Set(ref discussNote, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasDiscussInput));
                OnPropertyChanged(nameof(NeedsDiscussionInput));
                changed();
            }
        }
    }

    /// <summary>A reason chip is on, or the note has text.</summary>
    public bool HasDiscussInput => Reasons.Any(reason => reason.IsSelected) || !string.IsNullOrWhiteSpace(discussNote);

    /// <summary>The discussion card is open but still empty: the agent could not discuss anything yet.</summary>
    public bool NeedsDiscussionInput => discussing && !HasDiscussInput;

    /// <summary>The question is discussed, or its pick count is within bounds.</summary>
    public bool IsResolved => discussing || IsAnswered;

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

            if (HasFreeText && !otherSelected && (!MultiSelect || !IsAtMax))
            {
                SetOther(true);
                OnPicked();
            }

            RefreshState();
            changed();
        }
    }

    /// <summary>Not discussed, and the pick count is within [min, max] (the "other" row needs non-blank text).</summary>
    public bool IsAnswered =>
        !discussing && (!otherSelected || HasFreeText)
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

        // The reason is part of the request the agent receives, so it decides whether a discussion may start.
        OnPropertyChanged(nameof(HasDiscussInput));
        OnPropertyChanged(nameof(NeedsDiscussionInput));
        changed();
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
        OnPropertyChanged(nameof(HasDiscussInput));
        OnPropertyChanged(nameof(NeedsDiscussionInput));
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
        PreviousCommand = new RelayCommand(() => Move(-1), () => HasPrevious);
        NextCommand = new RelayCommand(() => Move(1), () => HasNext);
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

    /// <summary>Steps to the question before the visible one; the footer offers it whenever there is one.</summary>
    public RelayCommand PreviousCommand { get; }

    /// <summary>Steps to the question after the visible one; the last question swaps it for the submit.</summary>
    public RelayCommand NextCommand { get; }

    public bool HasPrevious => IndexOfCurrent() > 0;

    public bool HasNext => IndexOfCurrent() < Questions.Count - 1;

    public string AgentName => agentName;

    /// <summary>The bottom-group tab title.</summary>
    public string TabTitle => QuestionsStrings.TabTitle(agentName);

    /// <summary>One line: which session this is, and that the agent is waiting for an answer.</summary>
    public string HeaderText => QuestionsStrings.NeedsInput(SessionTitle, agentName);

    private int waitingCount;

    /// <summary>
    /// The card may be submitted: a question marked for discussion carries the reason or note the agent will answer, and
    /// every other question is answered — unless a discussion is under way, which submits even with questions still open
    /// (those come back with the next <c>ask_user</c> call, so the user need not answer everything before asking a question).
    /// </summary>
    public bool CanSubmit => !IsDecided && Questions.All(IsSettled);

    private bool IsSettled(QuestionItemViewModel question) =>
        question.IsDiscussing
            ? question.HasDiscussInput
            : question.IsAnswered || IsDiscussionRequested;

    /// <summary>
    /// The submit sits on the last question, and on any question whose discussion the user wants to start right here.
    /// </summary>
    public bool ShowsSubmit => !HasNext || IsDiscussionRequested;

    /// <summary>At least one question asks for a discussion: submitting starts it instead of finishing the questions.</summary>
    public bool IsDiscussionRequested => Questions.Any(question => question.IsDiscussing);

    public string SubmitText => IsDiscussionRequested ? QuestionsStrings.SubmitDiscuss : QuestionsStrings.Submit;

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
        RaiseStepChanged();
    }

    private void Move(int delta)
    {
        var index = IndexOfCurrent() + delta;
        if (index >= 0 && index < Questions.Count)
        {
            Select(Questions[index]);
        }
    }

    private int IndexOfCurrent()
    {
        for (var i = 0; i < Questions.Count; i++)
        {
            if (ReferenceEquals(Questions[i], current))
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>The footer's navigation pair follows the visible question, so every step re-reads both edges.</summary>
    private void RaiseStepChanged()
    {
        PreviousCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasPrevious));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(ShowsSubmit));
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
        OnPropertyChanged(nameof(ShowsSubmit));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(SubmitText));
        OnPropertyChanged(nameof(WaitingText));
        foreach (var question in Questions)
        {
            question.RefreshLanguage();
        }
    }
}
