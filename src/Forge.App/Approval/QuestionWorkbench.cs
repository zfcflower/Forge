using System.Collections.Specialized;
using Forge.App.Localization;
using Forge.App.Workbench;
using Forge.App.Workbench.Controls;

namespace Forge.App.Approval;

/// <summary>
/// Docks the shown <c>ask_user</c> question cards of the active session as tabs of the workbench's bottom group (the same
/// group terminals and sub-agent panels use), so a question pushes the transcript up instead of floating over it. A card
/// belongs to its session: switching sessions moves the tabs of other sessions out of the group (the card stays pending in the
/// queue) and switching back docks them again; a card that leaves the queue closes its tab.
/// <para>UI thread only.</para>
/// </summary>
internal sealed class QuestionWorkbench : IDisposable
{
    private readonly WorkbenchLayout layout;
    private readonly ApprovalQueue queue;
    private readonly Action revealBottomGroup;
    private readonly List<UserQuestionCardViewModel> tracked = [];
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private string? activeSessionId;
    private double? heightBefore;
    private double heightSet;
    private bool disposed;

    public QuestionWorkbench(WorkbenchLayout layout, ApprovalQueue queue, Action revealBottomGroup)
    {
        ArgumentNullException.ThrowIfNull(revealBottomGroup);
        this.layout = layout;
        this.queue = queue;
        this.revealBottomGroup = revealBottomGroup;
        queue.Questions.CollectionChanged += OnQuestionsChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public void OnActiveSessionChanged(string? sessionId)
    {
        activeSessionId = sessionId;
        Reconcile();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        queue.Questions.CollectionChanged -= OnQuestionsChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        foreach (var card in tracked)
        {
            CloseTabOf(card);
        }

        tracked.Clear();
        RestoreHeight();
    }

    private void OnQuestionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Reconcile();

    private void Reconcile()
    {
        if (disposed)
        {
            return;
        }

        // A card that left the queue (answered, dismissed or withdrawn) takes its tab with it, floating or not. When the
        // user decided it, the group folds back to its title bar: nothing is left to show, and the transcript gets the room
        // back. A card the queue simply moved on from (a withdrawal) leaves the group as the user had it.
        var decided = false;
        for (var i = tracked.Count - 1; i >= 0; i--)
        {
            if (!queue.Questions.Contains(tracked[i]))
            {
                decided |= tracked[i].IsDecided
                    && string.Equals(tracked[i].SessionId, activeSessionId, StringComparison.Ordinal);
                tracked[i].ContentHeightReported -= OnContentHeight;
                CloseTabOf(tracked[i]);
                tracked.RemoveAt(i);
            }
        }

        if (decided)
        {
            layout.IsBottomMinimized = true;
        }

        foreach (var card in queue.Questions)
        {
            if (!tracked.Contains(card))
            {
                tracked.Add(card);
                card.ContentHeightReported += OnContentHeight;
            }

            var tab = layout.FindTab(WorkbenchIds.QuestionTab(card.Id));
            var mine = string.Equals(card.SessionId, activeSessionId, StringComparison.Ordinal);
            if (mine && tab is null)
            {
                revealBottomGroup();
                layout.OpenTab(
                    layout.BottomTabs,
                    WorkbenchIds.QuestionTab(card.Id),
                    TabKind.Question,
                    card,
                    card.TabTitle,
                    activate: true,
                    closable: false);
            }
            else if (!mine && tab is not null)
            {
                // Another session's question never stays in this session's workspace, floating or docked back.
                layout.CloseTab(tab);
            }
        }

        // The user's own height comes back once the active session has no question left to show.
        var anyMine = false;
        foreach (var card in queue.Questions)
        {
            if (string.Equals(card.SessionId, activeSessionId, StringComparison.Ordinal))
            {
                anyMine = true;
                break;
            }
        }

        if (!anyMine)
        {
            RestoreHeight();
        }
    }

    /// <summary>
    /// Sizes the bottom group to exactly what the card needs (plus the panel's title bar), so the whole question is shown
    /// without scrolling inside a leftover height. The height the user had comes back once no question is left, and is only
    /// restored while the group still stands at the height this set.
    /// </summary>
    private void OnContentHeight(double content)
    {
        if (disposed)
        {
            return;
        }

        var wanted = content + PanelChrome.TitleBarHeight;
        if (Math.Abs(wanted - layout.BottomHeight) <= 1)
        {
            return;
        }

        heightBefore ??= layout.BottomHeight;
        heightSet = wanted;
        layout.BottomHeight = wanted;
    }

    private void RestoreHeight()
    {
        if (heightBefore is { } before && Math.Abs(layout.BottomHeight - heightSet) < 1)
        {
            layout.BottomHeight = before;
        }

        heightBefore = null;
    }

    private void CloseTabOf(UserQuestionCardViewModel card)
    {
        if (layout.FindTab(WorkbenchIds.QuestionTab(card.Id)) is { } tab)
        {
            layout.CloseTab(tab);
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (disposed || Environment.CurrentManagedThreadId != uiThread)
        {
            return;
        }

        foreach (var card in tracked)
        {
            if (layout.FindTab(WorkbenchIds.QuestionTab(card.Id)) is { } tab)
            {
                tab.Title = card.TabTitle;
            }
        }
    }
}
