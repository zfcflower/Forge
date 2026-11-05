using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Approval;

/// <summary>
/// The approval queue as the shell shows it in the bottom-right corner: one card per waiting tool call or staged
/// extension, collapsed to a header with the pending count. The view model owns the expand/collapse state and forwards the
/// queue's own observable list, so the queue stays the single owner of the cards.
/// <para>UI thread only: it is created by the composition root and driven by the queue's UI-thread events.</para>
/// </summary>
internal sealed class ApprovalQueueViewModel : ObservableObject, IDisposable
{
    private readonly ApprovalQueue queue;
    private bool isExpanded = true;
    private bool disposed;

    public ApprovalQueueViewModel(ApprovalQueue queue)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        ToggleCommand = new RelayCommand(Toggle);
        queue.CardAdded += OnCardAdded;
        queue.Changed += OnQueueChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>The queued cards; the same list the queue owns.</summary>
    public RangeObservableCollection<ApprovalCardViewModel> Items => queue.Items;

    public bool HasItems => queue.Items.Count > 0;

    /// <summary>Count next to the collapsed header.</summary>
    public int Count => queue.Items.Count;

    public string CountText => Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The queue starts expanded; collapsing leaves the header (and the count) on screen.</summary>
    public bool IsExpanded
    {
        get => isExpanded;
        private set
        {
            if (Set(ref isExpanded, value))
            {
                OnPropertyChanged(nameof(ToggleText));
            }
        }
    }

    public string ToggleText => Loc.Instance[IsExpanded ? ExtensionsStrings.QueueToggleCollapse : ExtensionsStrings.QueueToggleExpand];

    public RelayCommand ToggleCommand { get; }

    /// <summary>Opens the panel without changing a decision; the settings section uses it to reveal a card.</summary>
    public void Expand() => IsExpanded = true;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        queue.CardAdded -= OnCardAdded;
        queue.Changed -= OnQueueChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private void Toggle() => IsExpanded = !IsExpanded;

    private void OnCardAdded(ApprovalCardViewModel card) => IsExpanded = true;

    private void OnQueueChanged()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(ToggleText));
}
