using Avalonia.Controls;

namespace Forge.App.Views.Preview;

/// <summary>The notebook preview: virtualized cells with code sources, outputs and lazily decoded PNGs.</summary>
public partial class NotebookPreviewView : UserControl
{
    public NotebookPreviewView()
    {
        InitializeComponent();
    }
}
