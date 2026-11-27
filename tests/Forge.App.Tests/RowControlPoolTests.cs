using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.VisualTree;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests;

// Reuse is what keeps a drag cheap: a row that scrolls out hands its control to the row that scrolls in, instead of that row
// building a new control (a tool group's row costs 5.7 ms and 1.9 MB to build, a few hundred microseconds to re-bind). Every
// expectation here is about identity, data and the state a reused control must not carry over, so nothing depends on timings.
[Collection("forge-app-ui")]
public sealed class RowControlPoolTests : IDisposable
{
    // The cache is process-wide, so one test's leftovers are the next test's input: whichever method runs first would decide
    // whether a later one rents a control built from a different template. Each test therefore starts and ends empty.
    public RowControlPoolTests() => RowControlPool.Reset();

    public void Dispose() => RowControlPool.Reset();

    private sealed record Row(string Text);

    [Fact]
    public async Task The_next_row_of_a_type_takes_the_control_the_last_one_left()
    {
        await SharedUi.RunAsync(async () =>
        {
            RowControlPool.Enabled = true;
            try
            {
                var window = new Window { Width = 400, Height = 300 };
                var host = Host(Text());
                window.Content = host;
                window.Show();

                host.ItemsSource = new[] { new Row("one"), new Row("two") };
                SharedUi.Settle(window);
                var first = Controls(host);
                Assert.Equal(["one", "two"], first.ConvertAll(control => ((TextBlock)control).Text));

                // New items of the same type: the old rows' controls go back to the cache, and the new rows take them over.
                host.ItemsSource = new[] { new Row("three"), new Row("four") };
                SharedUi.Settle(window);
                var second = Controls(host);

                // Both halves matter: the control is the one that scrolled out (the cache handed it over), and it shows the
                // row it was handed to (the binding followed the data context).
                Assert.Equal(["three", "four"], second.ConvertAll(control => ((TextBlock)control).Text));
                Assert.All(second, control => Assert.Contains(control, first));
            }
            finally
            {
                RowControlPool.Enabled = true;
            }
        });
    }

    [Fact]
    public async Task A_control_waiting_in_the_cache_carries_no_data()
    {
        await SharedUi.RunAsync(async () =>
        {
            RowControlPool.Enabled = true;
            try
            {
                var row = new Row("kept");
                var window = new Window { Width = 400, Height = 300 };
                var host = Host(Text());
                window.Content = host;
                window.Show();

                host.ItemsSource = new[] { row };
                SharedUi.Settle(window);
                var control = Assert.Single(Controls(host));
                Assert.Same(row, control.DataContext);

                // The host lets the row go: nothing left in the cached control may keep pointing at it.
                host.ItemsSource = Array.Empty<Row>();
                SharedUi.Settle(window);
                Assert.Null(control.DataContext);
                Assert.Equal(0, RowControlPool.WaitingWithData);
            }
            finally
            {
                RowControlPool.Enabled = true;
            }
        });
    }

    [Fact]
    public async Task A_reused_control_starts_at_the_top_of_its_inner_viewer_with_nothing_selected()
    {
        await SharedUi.RunAsync(async () =>
        {
            RowControlPool.Enabled = true;
            try
            {
                var window = new Window { Width = 400, Height = 300 };
                var host = Host(new FuncDataTemplate<Row>((_, _) => new StackPanel
                {
                    Children =
                    {
                        new ScrollViewer { MaxHeight = 40, Content = new Border { Height = 900 } },
                        new SelectableTextBlock { [!TextBlock.TextProperty] = new Binding(nameof(Row.Text)) },
                    },
                }));
                window.Content = host;
                window.Show();

                host.ItemsSource = new[] { new Row("first row") };
                SharedUi.Settle(window);
                var control = Assert.Single(Controls(host));
                var viewer = control.GetVisualDescendants().OfType<ScrollViewer>().Single();
                var text = Assert.Single(control.GetVisualDescendants().OfType<SelectableTextBlock>());
                viewer.Offset = new Vector(0, 200);
                text.SelectionStart = 0;
                text.SelectionEnd = 3;
                SharedUi.Settle(window);
                Assert.True(viewer.Offset.Y > 0);
                Assert.True(text.SelectionEnd > 0);

                // The row leaves: the control goes back to the cache, cleared of what the row did to it.
                host.ItemsSource = Array.Empty<Row>();
                SharedUi.Settle(window);
                Assert.Null(control.DataContext);

                // It comes back as the next row: still cleared, and showing that row.
                host.ItemsSource = new[] { new Row("second row") };
                SharedUi.Settle(window);
                var reused = Assert.Single(Controls(host));
                Assert.Same(control, reused);
                var again = Assert.Single(reused.GetVisualDescendants().OfType<SelectableTextBlock>());
                Assert.Equal(new Vector(0, 0), reused.GetVisualDescendants().OfType<ScrollViewer>().Single().Offset);
                Assert.True(
                    again.SelectionStart == 0 && again.SelectionEnd == 0,
                    $"selection came back as {again.SelectionStart}..{again.SelectionEnd} after reuse");
                Assert.Equal("second row", again.Text);
            }
            finally
            {
                RowControlPool.Enabled = true;
            }
        });
    }

    private static PooledItemsControl Host(IDataTemplate template) => new() { DataTemplates = { template } };

    private static FuncDataTemplate<Row> Text() =>
        new((_, _) => new TextBlock { [!TextBlock.TextProperty] = new Binding(nameof(Row.Text)) });

    private static List<Control> Controls(ItemsControl host)
    {
        var controls = new List<Control>();
        for (var index = 0; index < host.Items.Count; index++)
        {
            if (host.ContainerFromIndex(index) is ContentPresenter { Child: { } child })
            {
                controls.Add(child);
            }
        }

        return controls;
    }
}
