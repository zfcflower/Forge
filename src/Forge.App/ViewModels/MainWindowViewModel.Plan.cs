namespace Forge.App.ViewModels;

/// <summary>
/// The window's plan panel slot (wave W7 owns this file). <see cref="Plan"/> is the overlay the window shows: null hides it,
/// an instance is drawn by the template registered for its type in <c>App.axaml</c>. The panel is created when it is opened
/// and disposed when it closes, so a closed panel holds no store or language subscription.
/// <para>The shell is also the panel's host (<see cref="IPlanPanelHost"/>): it names the session the panel reads, runs the
/// pull-request flow, and writes a hand-off message into that session's composer - the three things the panel needs and must
/// not own.</para>
/// </summary>
internal sealed partial class MainWindowViewModel : IPlanPanelHost
{
    private PlanPanelViewModel? plan;

    /// <summary>The plan overlay's view model; null while the panel is closed.</summary>
    public PlanPanelViewModel? Plan
    {
        get => plan;
        private set => Set(ref plan, value);
    }

    /// <summary>The title-bar plan button: opens the panel, or closes it when it is already open.</summary>
    public RelayCommand OpenPlanCommand { get; private set; } = null!;

    /// <summary>Called once from the constructor, before the window binds the overlay.</summary>
    private void InitializePlan()
    {
        OpenPlanCommand = new RelayCommand(TogglePlan, () => activeSession is not null);
        PropertyChanged += OnPlanActiveSessionChanged;
    }

    /// <summary>
    /// A session switch closes the panel: it reads one session's plan, and leaving the previous session's plan on screen
    /// would report the wrong checklist. The open command follows the same fact - without an active session there is nothing
    /// to show.
    /// </summary>
    private void OnPlanActiveSessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ActiveSession))
        {
            return;
        }

        OpenPlanCommand.NotifyCanExecuteChanged();
        ClosePlan();
    }

    private void TogglePlan()
    {
        if (plan is not null)
        {
            ClosePlan();
            return;
        }

        if (activeSession is null)
        {
            return;
        }

        var panel = new PlanPanelViewModel(composition.Todos, this);
        Plan = panel;
        panel.Open();
    }

    private void ClosePlan()
    {
        if (plan is not { } closing)
        {
            return;
        }

        Plan = null;
        closing.Dispose();
    }

    // ---------------------------------------------------------------- IPlanPanelHost

    PlanPanelSession? IPlanPanelHost.PlanSession =>
        activeSession is { } session
            ? new PlanPanelSession(session.SessionId, session.Title, session.IsReadOnlyView)
            : null;

    bool IPlanPanelHost.CanOpenPlanPullRequest => GitSync.CanComposePullRequest;

    void IPlanPanelHost.OpenPlanPullRequest()
    {
        // The same command the 提交目标 row runs, so the dialog, its body (this plan) and its checks stay one path.
        if (GitSync.ComposePullRequestCommand.CanExecute(null))
        {
            GitSync.ComposePullRequestCommand.Execute(null);
        }
    }

    void IPlanPanelHost.ClosePlan() => ClosePlan();

    /// <summary>
    /// Writes the hand-off message into the session's composer and sends it, or queues it while a turn runs. An unsent draft
    /// the user typed is never overwritten: the panel reports that instead. A read-only view, a handing-over session and a
    /// composer that cannot send (compacting, gated) all report "unavailable" and leave nothing behind.
    /// </summary>
    PlanHandoff IPlanPanelHost.HandoffToAgent(string prompt)
    {
        if (activeSession is not { } session || session.IsReadOnlyView || session.IsHandingOver)
        {
            return PlanHandoff.Unavailable;
        }

        if (!string.IsNullOrWhiteSpace(session.Draft))
        {
            return PlanHandoff.BlockedByDraft;
        }

        session.Draft = prompt;
        if (session.IsRunning)
        {
            if (!session.QueueCommand.CanExecute(null))
            {
                session.Draft = string.Empty;
                return PlanHandoff.Unavailable;
            }

            session.QueueCommand.Execute(null);
            return PlanHandoff.Queued;
        }

        if (!session.SendCommand.CanExecute(null))
        {
            session.Draft = string.Empty;
            return PlanHandoff.Unavailable;
        }

        session.SendCommand.Execute(null);
        return PlanHandoff.Sent;
    }
}
