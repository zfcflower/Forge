using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Forge.App.Appearance;

/// <summary>
/// Body of the <c>appearance</c> settings section. The family box is an editable auto-complete: typing only fills the
/// edit buffer, and the preference is written when the user picks a candidate, presses Enter or leaves the box (see
/// <see cref="FontGroupViewModel.CommitFamily"/>). The handlers are code-behind forwards on purpose - no reflection
/// binding, so the view stays Native AOT safe.
/// </summary>
public partial class AppearanceSettingsView : UserControl
{
    public AppearanceSettingsView()
    {
        InitializeComponent();

        // Enter is handled by the box itself while its drop-down is open (it commits the highlighted candidate and
        // raises SelectionChanged), and that class handler runs before any bubbling handler on the box. The tunnel phase
        // sees the key first - from the box and from its inner text box alike - so "type a family, press Enter" applies.
        UiFamilyBox.AddHandler(KeyDownEvent, OnFamilyKeyDown, RoutingStrategies.Tunnel);
        CodeFamilyBox.AddHandler(KeyDownEvent, OnFamilyKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Picking a candidate in the drop-down applies it; a selection the control cleared while typing does not.</summary>
    private void OnFamilySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GroupOf(sender) is { } group && e.AddedItems is [string { Length: > 0 } family, ..])
        {
            // The family is read from the selection, not from the binding: whether the text is updated before or after
            // this event is the control's business, the picked family is not.
            group.FamilyText = family;
            group.CommitFamily();
        }
    }

    private void OnFamilyLostFocus(object? sender, RoutedEventArgs e) => GroupOf(sender)?.CommitFamily();

    private void OnFamilyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is AutoCompleteBox { IsDropDownOpen: false } box)
        {
            GroupOf(box)?.CommitFamily();
        }
    }

    /// <summary>
    /// The group one of the two family boxes edits. The section view model is the data context of both boxes, so the box
    /// itself decides which half it belongs to - the names come from the markup, not from a template match.
    /// </summary>
    private FontGroupViewModel? GroupOf(object? sender) =>
        (DataContext, sender) switch
        {
            (AppearanceSettingsViewModel section, AutoCompleteBox box) when ReferenceEquals(box, UiFamilyBox) => section.Ui,
            (AppearanceSettingsViewModel section, AutoCompleteBox box) when ReferenceEquals(box, CodeFamilyBox) => section.Code,
            _ => null,
        };
}
