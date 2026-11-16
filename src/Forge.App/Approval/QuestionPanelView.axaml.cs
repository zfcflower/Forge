using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Forge.App.Approval;

/// <summary>
/// The body of a question tab in the bottom group: the whole card is the view model's state. The only thing the view
/// reports is how tall its content wants to be, so the bottom group can open tall enough to show every option.
/// </summary>
public partial class QuestionPanelView : UserControl
{
    private double reported;

    public QuestionPanelView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LayoutUpdated += OnLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        reported = 0;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (DataContext is not UserQuestionCardViewModel card)
        {
            return;
        }

        // Only the current question is visible, so the body's desired height is that question's; every part's desired size
        // already includes its margin.
        var height = HeaderPart.DesiredSize.Height
            + (ChipsPart.IsVisible ? ChipsPart.DesiredSize.Height : 0)
            + BodyPart.DesiredSize.Height
            + FooterPart.DesiredSize.Height;
        if (height > 0 && Math.Abs(height - reported) > 1)
        {
            reported = height;
            card.ReportContentHeight(height);
        }
    }
}
