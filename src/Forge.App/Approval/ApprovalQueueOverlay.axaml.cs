using Avalonia.Controls;

namespace Forge.App.Approval;

/// <summary>
/// The queue's view. It has no dependency on the root view model: it resolves the shell's <see cref="ApprovalServices"/>,
/// which the composition root creates before the window, and hides itself entirely when there is none (a test window or a
/// shell built without the approval wiring).
/// </summary>
public partial class ApprovalQueueOverlay : UserControl
{
    public ApprovalQueueOverlay()
    {
        InitializeComponent();
        if (ApprovalServices.Current is { } services)
        {
            DataContext = services.QueueViewModel;
            return;
        }

        IsVisible = false;
    }
}
