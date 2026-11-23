using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The tree row surface is shared by the project tree and the changed-files tree, and the two use it for different marks:
/// the project tree paints the row the user picked, the changed-files tree paints the file the editor has open through
/// `active-row`. Dropping that class as "the second highlight of the project tree" takes the changed-files mark with it,
/// and nothing in the changed-files view says so — the row just stops being highlighted.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TreeRowHighlightTests
{
    [Fact]
    public Task A_changed_file_row_marks_the_file_the_editor_has_open() => SharedUi.RunAsync(() =>
    {
        var styles = Assert.IsAssignableFrom<Styles>(
            AvaloniaXamlLoader.Load(new Uri("avares://Forge.App/Resources/TreeStyles.axaml")));
        var open = new Border { Classes = { "tree-row-content", "active-row" } };
        var plain = new Border { Classes = { "tree-row-content" } };
        var window = new Window
        {
            Width = 320,
            Height = 160,
            Content = new StackPanel { Children = { open, plain } },
        };
        window.Styles.Add(styles);
        window.Show();
        SharedUi.Settle(window);

        // The mark is one row's fill, so it has to resolve to a real colour, not the transparent base the plain row keeps.
        var mark = Assert.IsAssignableFrom<ISolidColorBrush>(open.Background);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(plain.Background).Color);
        Assert.NotEqual(Colors.Transparent, mark.Color);

        window.Close();
        return Task.CompletedTask;
    });
}
