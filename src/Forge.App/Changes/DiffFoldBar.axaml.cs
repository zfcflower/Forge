using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Forge.App.Localization;

namespace Forge.App.Changes;

internal sealed partial class DiffFoldBar : UserControl
{
    public DiffFoldBar()
    {
        InitializeComponent();
    }

    /// <summary>Raised when an expandable bar is clicked.</summary>
    public event EventHandler? ExpandRequested;

    public int FileIndex { get; private set; }

    public int Key { get; private set; }

    public bool CanExpand { get; private set; }

    public void Bind(in DiffRow row)
    {
        FileIndex = row.File;
        Key = row.Key;
        CanExpand = row.Expandable;
        var text = Loc.Instance.Format(DiffStrings.Unmodified, TextFormat.Number(row.Count));
        Label.Text = text;
        Chevron.IsVisible = row.Expandable;
        Bar.IsEnabled = row.Expandable;
        Bar.Cursor = row.Expandable ? new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) : null;
        AutomationProperties.SetName(Bar, text);
        ToolTip.SetTip(Bar, row.Expandable ? Loc.Instance[DiffStrings.UnmodifiedTip] : null);
    }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (CanExpand)
        {
            ExpandRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
