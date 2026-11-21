using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Settings;
using Forge.Core.Providers;
using Xunit;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Forge.App.Tests;

/// <summary>
/// The model metadata window's table. What is decided by hand here and easy to undo without noticing: a number cell rests as
/// text and builds its field only for the cell being edited (a live field per cell cost 275-390 ms to open and 260-300 ms per
/// screen of scrolling - bench <c>table.metadata.*</c>, and the table still carries 75-95 fields while resting otherwise), an
/// emptied cell means "unknown" and is stored as an absent field (a zero window or price would put a fake "% used" and
/// "$0.00" on every session), a separator drag moves the boundary the pointer is on, and the first column's header reads its
/// title before its select-all switch.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ModelMetadataWindowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-metadata-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient http = new();

    public void Dispose()
    {
        http.Dispose();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // The model catalog store may still hold a handle; the tree is only this test's temp folder.
        }
    }

    [Fact]
    public Task The_resting_table_holds_no_number_fields() => SharedUi.RunAsync(() =>
    {
        var (win, table, model) = Open();
        try
        {
            var cell = Cell(table, model.Rows[0], "context");
            Assert.Empty(table.GetVisualDescendants().OfType<NumericUpDown>());

            // A click builds exactly one field, in the clicked cell, with the stored number in it.
            Click(win, cell);
            var field = Field(cell);
            Assert.Single(table.GetVisualDescendants().OfType<NumericUpDown>());
            Assert.Equal(1000000m, field.Value);
            Assert.True(IsFocused(field));

            // Enter and Escape close it again; the cell is text again and holds the number.
            win.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            SharedUi.Settle(win);
            Assert.Empty(table.GetVisualDescendants().OfType<NumericUpDown>());
            Assert.Equal("1000000", Display(cell).Text);

            // Clicking another column closes it too, and only a click on a number column opens a new one.
            Click(win, cell);
            Assert.Single(table.GetVisualDescendants().OfType<NumericUpDown>());
            Click(win, Cell(table, model.Rows[0], "model"));
            Assert.Empty(table.GetVisualDescendants().OfType<NumericUpDown>());

            return Task.CompletedTask;
        }
        finally
        {
            win.Close();
        }
    });

    [Fact]
    public Task An_emptied_number_cell_stays_unknown_and_never_becomes_zero() => SharedUi.RunAsync(() =>
    {
        var (win, table, model) = Open();
        try
        {
            var row = model.Rows[0];
            var cell = Cell(table, row, "context");
            Assert.Equal("1000000", Display(cell).Text);

            Click(win, cell);
            var field = Field(cell);
            var box = field.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");

            // Typing writes through to the row, and text that is not a number never gets into the field.
            box.Text = "2000000";
            Assert.Equal(2000000m, row.ContextWindow);
            box.Text = "abc";
            Assert.Equal("2000000", box.Text);
            Assert.False(row.HasContextWindowError);

            // A window of zero is not a window: the row reports it and the resting cell draws it once the field closes.
            box.Text = "0";
            Assert.True(row.HasContextWindowError);

            // Clearing the box is "unknown": no number, and the cell shows nothing rather than a zero.
            box.Text = string.Empty;
            Assert.Null(row.ContextWindow);
            Click(win, Cell(table, row, "model"));
            Assert.Equal(string.Empty, Display(cell).Text);

            // What gets written back is an absent field, never a zero window.
            var section = ModelMetadataViewModel.ToOverrides(model.Rows)[row.Model];
            Assert.Null(section.ContextWindow);
            Assert.Equal(0.3, section.Input);

            return Task.CompletedTask;
        }
        finally
        {
            win.Close();
        }
    });

    [Fact]
    public Task A_price_that_cannot_be_saved_is_red_on_the_cell_and_on_its_field() => SharedUi.RunAsync(() =>
    {
        var (win, table, model) = Open();
        try
        {
            var row = model.Rows[0];
            var cell = Cell(table, row, "input");
            Assert.DoesNotContain("invalid", Line(cell).Classes);
            Assert.Same(Resource(win, "BorderBrush"), Line(cell).BorderBrush);

            // One price of the package gone: the other three cannot be saved on their own (the writer would fill the gap with
            // zero), so the price cells of that row turn red and the save gate reads it.
            var price = row.Input;
            row.Input = null;
            SharedUi.Settle(win);
            Assert.Contains("invalid", Line(cell).Classes);
            Assert.Same(Resource(win, "StatusErrorBrush"), Line(cell).BorderBrush);
            Assert.Equal(new Thickness(1), Line(cell).BorderThickness);
            Assert.True(row.HasErrors);

            // The field a cell opens carries the same red frame: its appearance comes from the field sheet scoped to the one
            // control, not from the window-wide sheet the table cannot afford.
            Click(win, cell);
            var field = Field(cell);
            Assert.Contains("invalid", field.Classes);
            Assert.Same(Resource(win, "StatusErrorBrush"), Frame(field).BorderBrush);
            Assert.Same(Resource(win, "StatusErrorBrush"), Edge(field).Background);

            // Filling it again clears the colour on both, and the gate.
            field.Value = price;
            SharedUi.Settle(win);
            Assert.Equal(0.3m, row.Input);
            Assert.False(row.HasErrors);
            Assert.DoesNotContain("invalid", field.Classes);
            Assert.Same(Resource(win, "BorderBrush"), Frame(field).BorderBrush);
            Click(win, Cell(table, row, "model"));
            Assert.DoesNotContain("invalid", Line(cell).Classes);

            return Task.CompletedTask;
        }
        finally
        {
            win.Close();
        }
    });

    [Fact]
    public Task A_separator_drag_moves_the_boundary_the_pointer_is_on() => SharedUi.RunAsync(() =>
    {
        var (win, table, model) = Open();
        try
        {
            var columns = table.Columns!;
            var fill = columns.Single(column => column.Width.IsStar);
            var fillWidth = fill.ActualWidth;

            // Every separator right of the model column used to be paid for by that column: the dragged cell grew to the
            // left while the pointer went right, and the line under the pointer did not move at all.
            var input = columns[columns.IndexOf(fill) + 4];
            var header = table.GetVisualDescendants().OfType<TableViewColumnHeader>()
                .Single(h => ReferenceEquals(h.Column, input));
            var thumb = header.GetVisualDescendants().OfType<Thumb>().Single(t => t.Name == "PART_Resizer");
            var grab = thumb.TranslatePoint(new Point(4, thumb.Bounds.Height / 2), win)!.Value;
            var edge = RightEdge(header, win);
            var width = input.ActualWidth;

            win.MouseDown(grab, MouseButton.Left);
            win.MouseMove(grab + new Vector(30, 0));
            SharedUi.Settle(win);
            win.MouseUp(grab + new Vector(30, 0), MouseButton.Left);
            SharedUi.Settle(win);

            Assert.Equal(width + 30, input.ActualWidth, 1);
            Assert.Equal(edge + 30, RightEdge(header, win), 1);
            Assert.Equal(fillWidth, fill.ActualWidth, 1);

            // The width nobody claimed comes back to the model column when the drag ends, so a table made narrower than the
            // view has no strip of window background inside its frame.
            var moved = thumb.TranslatePoint(new Point(4, thumb.Bounds.Height / 2), win)!.Value;
            win.MouseDown(moved, MouseButton.Left);
            win.MouseMove(moved - new Vector(30, 0));
            SharedUi.Settle(win);
            win.MouseUp(moved - new Vector(30, 0), MouseButton.Left);
            SharedUi.Settle(win);

            Assert.Equal(width, input.ActualWidth, 1);
            Assert.True(fill.Width.IsStar);
            Assert.Equal(width + fillWidth, input.ActualWidth + fill.ActualWidth, 1);

            return Task.CompletedTask;
        }
        finally
        {
            win.Close();
        }
    });

    [Fact]
    public Task An_editable_cell_is_tinted_and_a_read_only_cell_is_not() => SharedUi.RunAsync(() =>
    {
        var (win, table, model) = Open();
        try
        {
            // The five number columns are the editable ones, and the pale Tiffany tint is how the row says so.
            foreach (var name in new[] { "context", "input" })
            {
                Assert.Same(Resource(win, "TiffanyTintBrush"), Line(Cell(table, model.Rows[0], name)).Background);
            }

            // The read-only columns keep the row's own background (no cell fill at all).
            Assert.Null(Line(Cell(table, model.Rows[0], "model")).Background);

            return Task.CompletedTask;
        }
        finally
        {
            win.Close();
        }
    });

    [Fact]
    public Task The_arrow_keys_carry_the_editor_from_cell_to_cell() => SharedUi.RunAsync(() =>
    {
        var (win, table, model) = Open();
        try
        {
            var first = model.Rows[0];
            var second = model.Rows[1];
            var context = Cell(table, first, "context");
            Click(win, context);
            Part(Field(context)).Text = "2000000";

            // Down commits what was typed and opens the field of the cell below - one field, in the new cell.
            Press(win, Key.Down);
            Assert.Equal(2000000m, first.ContextWindow);
            Assert.Empty(context.GetVisualDescendants().OfType<NumericUpDown>());
            var below = Cell(table, second, "context");
            Assert.True(Part(Field(below)).IsFocused);
            Assert.Equal(string.Empty, Display(below).Text);
            Assert.Single(table.GetVisualDescendants().OfType<NumericUpDown>());

            // The second row is the last one: Down has nowhere to go and leaves the field open, Enter commits and closes.
            Press(win, Key.Down);
            Assert.Single(below.GetVisualDescendants().OfType<NumericUpDown>());
            Press(win, Key.Enter);
            Assert.Empty(table.GetVisualDescendants().OfType<NumericUpDown>());
            Assert.True(IsCurrent(below));

            // Enter again opens the highlighted cell, Tab carries the editing one column right, Shift+Tab back.
            Press(win, Key.Enter);
            Assert.Single(below.GetVisualDescendants().OfType<NumericUpDown>());
            Press(win, Key.Tab);
            var input = Cell(table, second, "input");
            Assert.True(Part(Field(input)).IsFocused);
            Press(win, Key.Tab, KeyModifiers.Shift);
            Assert.True(Part(Field(below)).IsFocused);

            // Escape closes the field and leaves the table as text; the highlighted cell stays where it was.
            Press(win, Key.Escape);
            Assert.Empty(table.GetVisualDescendants().OfType<NumericUpDown>());
            Assert.True(IsCurrent(below));

            return Task.CompletedTask;
        }
        finally
        {
            win.Close();
        }
    });

    [Fact]
    public Task Arrow_keys_walk_the_highlighted_cell_while_the_table_is_text() => SharedUi.RunAsync(() =>
    {
        var (win, table, model) = Open();
        try
        {
            var first = model.Rows[0];
            var second = model.Rows[1];
            var context = Cell(table, first, "context");
            Click(win, context);
            Press(win, Key.Escape);

            // Left/right step along the five editable columns, down/up along the rows.
            Press(win, Key.Right);
            var input = Cell(table, first, "input");
            Assert.True(IsCurrent(input));
            Assert.False(IsCurrent(context));

            Press(win, Key.Down);
            var below = Cell(table, second, "input");
            Assert.True(IsCurrent(below));

            Press(win, Key.Left);
            Assert.True(IsCurrent(Cell(table, second, "context")));

            // Five steps right from the first editable column come back to it (the row is the last one, so the wrap stays).
            for (var step = 0; step < 5; step++)
            {
                Press(win, Key.Right);
            }

            Assert.True(IsCurrent(Cell(table, second, "context")));

            // Up walks back to the first row and stops there.
            Press(win, Key.Up);
            Press(win, Key.Up);
            Assert.True(IsCurrent(Cell(table, first, "context")));

            // Enter opens the highlighted cell without a pointer.
            Press(win, Key.Enter);
            Assert.Single(table.GetVisualDescendants().OfType<NumericUpDown>());

            return Task.CompletedTask;
        }
        finally
        {
            win.Close();
        }
    });

    [Fact]
    public Task The_editor_keeps_the_caret_arrows_until_the_text_ends() => SharedUi.RunAsync(() =>
    {
        var (win, table, model) = Open();
        try
        {
            var row = model.Rows[0];
            var context = Cell(table, row, "context");
            Click(win, context);
            var box = Field(context).GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");

            // In the middle of the number the arrow moves the caret, not the cell.
            box.CaretIndex = 3;
            box.SelectionStart = 3;
            box.SelectionEnd = 3;
            Press(win, Key.Right);
            Assert.Equal(4, box.CaretIndex);
            Assert.True(IsCurrent(context));

            // At the end of the text it leaves the field: the next cell opens with its own value.
            box.CaretIndex = box.Text!.Length;
            box.SelectionStart = box.CaretIndex;
            box.SelectionEnd = box.CaretIndex;
            Press(win, Key.Right);
            var input = Cell(table, row, "input");
            Assert.True(IsCurrent(input));
            Assert.Single(input.GetVisualDescendants().OfType<NumericUpDown>());

            return Task.CompletedTask;
        }
        finally
        {
            win.Close();
        }
    });

    [Fact]
    public Task The_visibility_header_reads_its_title_before_the_switch() => SharedUi.RunAsync(() =>
    {
        var (win, table, model) = Open();
        try
        {
            var header = table.GetVisualDescendants().OfType<TableViewColumnHeader>()
                .Single(h => ReferenceEquals(h.Column, table.Columns![0]));
            var panel = Assert.IsType<StackPanel>(header.Content);
            Assert.Equal(2, panel.Children.Count);
            var title = Assert.IsType<TextBlock>(panel.Children[0]);
            Assert.Equal(model.VisibilityHeader, title.Text);
            var check = Assert.IsType<CheckBox>(panel.Children[1]);
            Assert.Equal("model-metadata-all-shown", AutomationProperties.GetAutomationId(check));
            Assert.True(check.IsChecked);

            // The switch still only mirrors the rows: unchecking one row clears it, and the other rows keep their own state.
            model.Rows[0].IsShown = false;
            SharedUi.Settle(win);
            Assert.NotEqual(true, check.IsChecked);
            Assert.False(model.Rows[0].IsShown);
            Assert.True(model.Rows[1].IsShown);

            return Task.CompletedTask;
        }
        finally
        {
            win.Close();
        }
    });

    /// <summary>
    /// The window over a provider whose first model has a window and four prices. The visibility column only exists when the
    /// window was given the preferences, so one of each is passed.
    /// </summary>
    private (Window Window, TableView Table, ModelMetadataViewModel Model) Open()
    {
        Directory.CreateDirectory(directory);
        var services = new ProviderServices(Path.Combine(directory, "settings.json"), directory, http);
        var overrides = new Dictionary<string, ModelMetadata>(StringComparer.Ordinal)
        {
            ["model-a"] = new(1000000, new ModelPricing(0.3, 15, 0.03, 0.375)),
        };
        var definition = new ProviderDefinition
        {
            Id = "probe",
            PresetId = "custom",
            DisplayName = "Probe",
            Protocol = ProviderProtocol.OpenAiCompatible,
            BaseUrl = "https://example.invalid",
            CredentialKind = ProviderCredentialKind.ApiKey,
            Models = ["model-a", "model-b"],
            DefaultModel = "model-a",
            Overrides = overrides,
        };
        var provider = new ConnectedProvider(definition, services.Secrets, services.Cache, services.Credentials);
        var model = new ModelMetadataViewModel(services, provider, new AppPreferences());
        var window = new ModelMetadataWindow(model);
        window.Show();
        SharedUi.Settle(window);

        var table = window.GetVisualDescendants().OfType<TableView>().Single();
        Assert.Equal(2, model.Rows.Count);
        return (window, table, model);
    }

    /// <summary>Clicks the middle of a cell, which is how a cell is opened for editing.</summary>
    private static void Click(Window window, TableViewCell cell)
    {
        var middle = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window)!.Value;
        window.MouseDown(middle, MouseButton.Left);
        window.MouseUp(middle, MouseButton.Left);
        SharedUi.Settle(window);
    }

    /// <summary>Sends one key to the window and lets the layout settle.</summary>
    private static void Press(Window window, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        window.KeyPress(key, modifiers == KeyModifiers.None ? RawInputModifiers.None : RawInputModifiers.Shift, PhysicalKey.None, null);
        SharedUi.Settle(window);
    }

    /// <summary>The cell the arrow keys move from.</summary>
    private static bool IsCurrent(TableViewCell cell) => Line(cell).Classes.Contains("current");

    /// <summary>The field's own text box, which is where the caret and the keyboard live.</summary>
    private static TextBox Part(NumericUpDown field) =>
        field.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");

    /// <summary>The field a cell opened, which exists only while that cell is being edited.</summary>
    private static NumericUpDown Field(TableViewCell cell) => cell.GetVisualDescendants().OfType<NumericUpDown>().Single();

    /// <summary>The resting text of a number cell.</summary>
    private static TextBlock Display(TableViewCell cell) => cell.GetVisualDescendants().OfType<TextBlock>()
        .Single(text => text.Name == "ValueText");

    /// <summary>The cell's own frame, which is where an invalid cell shows its red border.</summary>
    private static Border Line(TableViewCell cell) => cell.GetVisualDescendants().OfType<Border>()
        .First(border => border.Classes.Contains("meta-line"));

    private static bool IsFocused(NumericUpDown field) =>
        field.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox").IsFocused;

    /// <summary>The number field's own frame: the stepper draws the outline, the cell's inner text box draws none.</summary>
    private static Border Frame(NumericUpDown field) => FieldBorder(field, "PART_BorderElement");

    /// <summary>The field's bottom edge, the heavier line that turns red on an invalid cell.</summary>
    private static Border Edge(NumericUpDown field) => FieldBorder(field, "PART_EdgeLine");

    /// <summary>
    /// One part of the field's own frame. The inner text box carries a copy of both parts (hidden, because the field is one
    /// control), so the search skips anything inside a text box.
    /// </summary>
    private static Border FieldBorder(NumericUpDown field, string name)
        => field.GetVisualDescendants().OfType<Border>()
            .First(b => b.Name == name && b.FindAncestorOfType<TextBox>() is null);

    /// <summary>The first row's cell of one column, in the order the window builds its columns.</summary>
    private static TableViewCell Cell(TableView table, ModelMetadataRowViewModel row, string name)
    {
        var columns = table.Columns!;
        var index = columns.IndexOf(columns.Single(column => column.Width.IsStar)) + (name switch
        {
            "context" => 3,
            "input" => 4,
            "output" => 5,
            "model" => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such column"),
        });
        var column = columns[index];
        return table.GetVisualDescendants().OfType<TableViewCell>()
            .Single(cell => ReferenceEquals(cell.Column, column) && ReferenceEquals(cell.DataContext, row));
    }

    private static double RightEdge(Visual header, Visual anchor)
        => header.TranslatePoint(new Point(header.Bounds.Width, 0), anchor)!.Value.X;

    /// <summary>The named resource of the window's current theme; a missing key fails here instead of comparing two nulls.</summary>
    private static IBrush Resource(Visual anchor, string key)
    {
        Assert.True(anchor.TryFindResource(key, anchor.ActualThemeVariant, out var value), "missing resource: " + key);
        return Assert.IsAssignableFrom<IBrush>(value);
    }
}
