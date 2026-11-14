using Avalonia.Controls;

namespace Forge.App.Views.Preview;

/// <summary>The image preview: the decoded bitmap at its (possibly scaled) size inside a scrollable area.</summary>
public partial class ImagePreviewView : UserControl
{
    public ImagePreviewView()
    {
        InitializeComponent();
    }
}
