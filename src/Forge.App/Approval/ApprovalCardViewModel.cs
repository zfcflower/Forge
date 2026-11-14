using System.Globalization;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Approval;

/// <summary>What the user did with one card.</summary>
internal enum ApprovalCardDecision
{
    Allow,
    Deny,

    /// <summary>
    /// The card left the queue without a user decision: a newer draft replaced it, or its owner went away. Nobody may act
    /// on it (a superseded plugin card must neither commit nor discard, or it would delete the newer draft).
    /// </summary>
    Superseded,

    /// <summary>
    /// The card left the queue without a user decision because its session was released, the app is shutting down or the
    /// waiter was cancelled. For a plugin card this is not a rejection: the staged draft stays pending.
    /// </summary>
    Withdrawn,
}

/// <summary>
/// The result of one card: allow or deny, plus the scope whose "remember" button was used (<see langword="null"/> when the
/// user just allowed once), plus - for a script run's batched card - whether the user allowed the rest of that run's calls
/// of the same tool. Extension cards use only the decision.
/// </summary>
internal readonly record struct ApprovalCardOutcome(
    ApprovalCardDecision Decision,
    ApprovalScope? Remember = null,
    bool AllowRestOfRun = false)
{
    public static ApprovalCardOutcome Allow { get; } = new(ApprovalCardDecision.Allow);

    public static ApprovalCardOutcome Deny { get; } = new(ApprovalCardDecision.Deny);

    public bool IsAllowed => Decision == ApprovalCardDecision.Allow;

    public bool IsSuperseded => Decision == ApprovalCardDecision.Superseded;

    public bool IsWithdrawn => Decision == ApprovalCardDecision.Withdrawn;

    /// <summary>The user did not decide: nobody may act on the card (neither commit nor discard).</summary>
    public bool IsUndecided => Decision is ApprovalCardDecision.Superseded or ApprovalCardDecision.Withdrawn;
}

/// <summary>
/// One entry of <see cref="ApprovalQueue"/>: a tool call card or a plugin approval card. The card owns its
/// <see cref="TaskCompletionSource{TResult}"/> (continuations never run inline on the UI thread) and its own decision
/// commands, so the queue only has to keep the list and hand the card's task back to the waiter.
/// <para>Cards are created on whatever thread produced the request and are shown on the UI thread. The decision itself is
/// raised by a command that only exists on the UI thread, except for the withdrawal paths (cancellation, session release),
/// which complete the card from a background thread; those set <see cref="IsDecided"/> without a notification, because a
/// bound-property change from a background thread is not allowed.</para>
/// </summary>
internal abstract class ApprovalCardViewModel : NotifyingObject, IDisposable
{
    private readonly TaskCompletionSource<ApprovalCardOutcome> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly DateTimeOffset createdAt;
    private EventHandler? languageChanged;
    private bool decided;
    private bool detached;

    protected ApprovalCardViewModel(string id, string sessionId, string sessionTitle, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        SessionId = sessionId ?? string.Empty;
        SessionTitle = string.IsNullOrWhiteSpace(sessionTitle) ? SessionId : sessionTitle;
        this.createdAt = createdAt;
    }

    /// <summary>Stable identity while the card is queued; a plugin card is keyed by its plugin id so it is never queued twice.</summary>
    public string Id { get; }

    public string SessionId { get; }

    /// <summary>The main session that produced the card; a plugin card from a previous run has no session.</summary>
    public string SessionTitle { get; }

    /// <summary>When the card was raised; the session watch reports approvals left waiting too long.</summary>
    public DateTimeOffset CreatedAt => createdAt;

    public string CreatedAtText => createdAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The waiter's task; completed exactly once, with <see cref="ApprovalCardOutcome.Deny"/> on withdrawal.</summary>
    public Task<ApprovalCardOutcome> Decision => completion.Task;

    public bool IsDecided => decided;

    /// <summary>
    /// What a waiter sees when the card is removed without a user decision. A tool card answers a denial (the call fails
    /// closed); a plugin card overrides this so a withdrawal never discards its staged draft.
    /// </summary>
    protected virtual ApprovalCardOutcome WithdrawalOutcome => ApprovalCardOutcome.Deny;

    /// <summary>The outcome the queue completes this card with when it removes it without a user decision.</summary>
    internal ApprovalCardOutcome WithdrawnOutcome => WithdrawalOutcome;

    /// <summary>Completes the card once; a late click after a withdrawal changes nothing.</summary>
    public bool TryComplete(ApprovalCardOutcome outcome)
    {
        if (!completion.TrySetResult(outcome))
        {
            return false;
        }

        decided = true;
        Detach();
        return true;
    }

    /// <summary>Unsubscribes from the live string table; called when the card leaves the queue.</summary>
    public void Dispose()
    {
        completion.TrySetResult(WithdrawalOutcome);
        decided = true;
        Detach();
    }

    /// <summary>
    /// Cards with localized, formatted text subscribe here: a language switch re-reads those strings. Static captions are
    /// bound straight to <see cref="Loc"/> in XAML and need no subscription.
    /// </summary>
    protected void AttachLanguageChanged(EventHandler handler)
    {
        languageChanged = handler;
        Loc.Instance.LanguageChanged += handler;
    }

    private void Detach()
    {
        if (detached)
        {
            return;
        }

        detached = true;
        if (languageChanged is { } handler)
        {
            Loc.Instance.LanguageChanged -= handler;
            languageChanged = null;
        }
    }
}
