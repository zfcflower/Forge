using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Tests.Localization;
using Forge.App.Tests.Workbench;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Palette;

/// <summary>
/// The palette as the window hosts it: the query line takes the focus when the palette opens, the arrows and Escape and
/// Enter reach the palette from inside the text box, and typing filters the rows it lists. Every key press here is a real
/// headless input event, so what is pinned is the routing the code-behind owns, not a method call the test made itself.
/// </summary>
[Collection("forge-app-ui")]
public sealed class PaletteViewTests
{
    private const double WindowWidth = 1200;
    private const double WindowHeight = 800;

    [Fact]
    public Task Opening_the_palette_puts_the_focus_on_the_query_box() => Ui(() =>
    {
        using var scene = Scene.Show(HeldFiles("src/Main.cs", "src/Other.cs"));

        scene.Open(PaletteSource.Files);

        Assert.True(scene.Query.IsFocused, "typing has to go to the palette without a click");
        Assert.True(scene.ViewModel.IsOpen);
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_arrow_keys_move_the_selection_and_stop_at_both_ends() => Ui(() =>
    {
        using var scene = Scene.Show(HeldFiles("a.cs", "b.cs", "c.cs"));
        scene.Open(PaletteSource.Files);
        Assert.Equal("a.cs", scene.ViewModel.SelectedItem?.Title);

        scene.Press(PhysicalKey.ArrowDown);
        Assert.Equal("b.cs", scene.ViewModel.SelectedItem?.Title);

        scene.Press(PhysicalKey.ArrowDown);
        scene.Press(PhysicalKey.ArrowDown);
        Assert.Equal("c.cs", scene.ViewModel.SelectedItem?.Title);

        scene.Press(PhysicalKey.ArrowUp);
        Assert.Equal("b.cs", scene.ViewModel.SelectedItem?.Title);

        scene.Press(PhysicalKey.ArrowUp);
        scene.Press(PhysicalKey.ArrowUp);
        Assert.Equal("a.cs", scene.ViewModel.SelectedItem?.Title);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Enter_runs_the_selected_row_once_and_escape_closes_a_reopened_palette() => Ui(() =>
    {
        using var scene = Scene.Show(HeldFiles("src/Main.cs", "src/Other.cs"));
        scene.Open(PaletteSource.Files);

        scene.Press(PhysicalKey.Enter);

        var activation = Assert.Single(scene.Host.Activations);
        Assert.Equal("Main.cs", activation.Item.Title);
        Assert.False(scene.ViewModel.IsOpen);

        scene.Open(PaletteSource.Files);
        Assert.True(scene.ViewModel.IsOpen);

        scene.Press(PhysicalKey.Escape);

        Assert.False(scene.ViewModel.IsOpen);
        Assert.Single(scene.Host.Activations);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Typing_filters_the_visible_rows_and_an_unmatched_query_shows_the_empty_state() => Ui(() =>
    {
        using var language = new LanguageScope(UiLanguage.English);
        using var scene = Scene.Show(HeldFiles("src/Main.cs", "docs/readme.md"));
        scene.Open(PaletteSource.Files);
        Assert.Equal(["Main.cs", "readme.md"], scene.RowTitles());
        Assert.False(scene.Empty.IsEffectivelyVisible);

        scene.Window.KeyTextInput("Main");
        SharedUi.Settle(scene.Window);

        Assert.Equal("Main", scene.Query.Text);
        Assert.Equal(["Main.cs"], scene.RowTitles());

        scene.Query.Text = "zzz";
        SharedUi.Settle(scene.Window);

        Assert.Empty(scene.RowTitles());
        Assert.True(scene.Empty.IsEffectivelyVisible);
        Assert.Equal(Loc.Instance[PaletteStrings.EmptyFiles], scene.Empty.Text);

        scene.Query.Text = "read";
        SharedUi.Settle(scene.Window);

        Assert.Equal(["readme.md"], scene.RowTitles());
        Assert.False(scene.Empty.IsEffectivelyVisible);
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_source_strip_lists_the_five_source_words_and_marks_the_current_one() => Ui(() =>
    {
        using var language = new LanguageScope(UiLanguage.English);
        using var scene = Scene.Show(HeldFiles("src/Main.cs"));

        scene.Open(PaletteSource.Files);

        var strip = WorkbenchUi.Find<ListBox>(scene.Window, "palette-sources");
        var labels = strip.GetVisualDescendants()
            .OfType<ListBoxItem>()
            .Select(static item => item.GetVisualDescendants().OfType<TextBlock>().First().Text ?? string.Empty)
            .ToArray();
        Assert.Equal(
            [
                Loc.Instance[PaletteStrings.SourceFiles],
                Loc.Instance[PaletteStrings.SourceContent],
                Loc.Instance[PaletteStrings.SourceSessions],
                Loc.Instance[PaletteStrings.SourceCommands],
                Loc.Instance[PaletteStrings.SourceSymbols],
            ],
            labels);
        var selected = Assert.IsType<PaletteSourceOption>(strip.SelectedItem);
        Assert.Equal(PaletteSource.Files, selected.Source);
        return Task.CompletedTask;
    });

    /// <summary>A file host whose refresh never answers, so the rows the view lists stay the cached ones.</summary>
    private static FakePaletteHost HeldFiles(params string[] files)
    {
        var host = new FakePaletteHost { CachedFiles = files, FilesAnswer = files };
        host.HoldFilesRefresh();
        return host;
    }

    private static Task Ui(Func<Task> body) => SharedUi.RunAsync(body);

    /// <summary>
    /// One shown palette over one host, hosted the way <c>MainWindow</c> hosts it: the view sits in a panel whose
    /// visibility follows <see cref="PaletteViewModel.IsOpen"/>, so a closed palette is really out of sight.
    /// </summary>
    private sealed class Scene : IDisposable
    {
        private Scene(Window window, PaletteViewModel viewModel, FakePaletteHost host)
        {
            Window = window;
            ViewModel = viewModel;
            Host = host;
        }

        public Window Window { get; }

        public PaletteViewModel ViewModel { get; }

        public FakePaletteHost Host { get; }

        public TextBox Query => WorkbenchUi.Find<TextBox>(Window, "palette-query");

        public TextBlock Empty => WorkbenchUi.Find<TextBlock>(Window, "palette-empty");

        public static Scene Show(FakePaletteHost host, AppPreferences? preferences = null)
        {
            var viewModel = new PaletteViewModel(host, preferences ?? new AppPreferences());
            var panel = new Panel { DataContext = viewModel, Children = { new PaletteView { DataContext = viewModel } } };
            panel.Bind(Visual.IsVisibleProperty, new Binding(nameof(PaletteViewModel.IsOpen)));
            var window = SharedUi.Show(panel, WindowWidth, WindowHeight);
            SharedUi.Settle(window);
            return new Scene(window, viewModel, host);
        }

        public void Open(PaletteSource source)
        {
            ViewModel.Open(source);
            SharedUi.Settle(Window);
        }

        public void Press(PhysicalKey key)
        {
            Window.KeyPressQwerty(key, RawInputModifiers.None);
            SharedUi.Settle(Window);
        }

        /// <summary>The rows the list really renders, in order, read off the realized item containers.</summary>
        public string[] RowTitles()
        {
            var list = WorkbenchUi.Find<ListBox>(Window, "palette-results");
            return
            [
                .. list.GetVisualDescendants()
                    .OfType<ListBoxItem>()
                    .Where(static item => item.IsEffectivelyVisible)
                    .Select(static item => Assert.IsType<PaletteItem>(item.DataContext).Title),
            ];
        }

        public void Dispose()
        {
            Window.Close();
            ViewModel.Dispose();
        }
    }
}
