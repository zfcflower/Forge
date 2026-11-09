using Avalonia.Controls;

namespace Forge.App.Changes;

internal sealed partial class DiffBlockBar : UserControl
{
    /// <summary>Fixed height of the bar; the diff editor reserves exactly this much for its placeholder row.</summary>
    public const double BarHeight = 28;

    public DiffBlockBar()
    {
        InitializeComponent();
    }
}
