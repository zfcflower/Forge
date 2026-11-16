using Avalonia.Controls;

namespace Forge.App.Views;

public partial class SubAgentView : UserControl
{
    public SubAgentView()
    {
        InitializeComponent();
        if (this.FindControl<ListBox>("Transcript") is { } transcript)
        {
            TranscriptFollower.Attach(transcript);
        }
    }
}
