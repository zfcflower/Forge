using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.App.Workbench.Controls;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The composer bar's own button tier and its icons: the model and orchestration chips and the attach and history buttons all
/// wear one light fill with no outline, the attach button carries the paperclip and the history button the clock-arrow, and
/// the model chip's label is the same size as the orchestration chip's (the two sit side by side, so a size between them
/// reads as a hierarchy that is not there).
/// </summary>
[Collection("forge-app-ui")]
public sealed class ComposerBarStyleTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-composer-bar-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient http = new();

    public void Dispose()
    {
        http.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then.
        }
    }

    [Fact]
    public Task Every_button_of_the_bar_wears_the_light_fill_and_no_outline() => SharedUi.RunAsync(async () =>
    {
        var session = await SessionAsync();
        var (window, view) = Show(session);

        foreach (var id in new[] { "composer-attach-images", "composer-history", "composer-model", "composer-orchestration" })
        {
            var button = Button(view, id);
            var light = Assert.IsAssignableFrom<ISolidColorBrush>(Resource(button, "SurfaceSubtleBrush"));
            Assert.Equal(light.Color, Assert.IsAssignableFrom<ISolidColorBrush>(button.Background).Color);
            Assert.Equal(default, button.BorderThickness);
        }

        // The empty draft leaves the send button out; it is not part of this tier, so nothing to compare here.
        Assert.NotNull(Button(view, "composer-history"));

        session.Dispose();
        window.Close();
    });

    [Fact]
    public Task The_attach_and_history_buttons_carry_their_own_glyphs() => SharedUi.RunAsync(async () =>
    {
        var session = await SessionAsync();
        var (window, view) = Show(session);

        var attach = Button(view, "composer-attach-images");
        Assert.Same(Resource(attach, "IconAttach"), Icon(attach));
        var history = Button(view, "composer-history");
        Assert.Same(Resource(history, "IconChatHistory"), Icon(history));

        session.Dispose();
        window.Close();
    });

    [Fact]
    public Task The_model_chip_label_is_the_same_size_as_the_orchestration_chip_label() => SharedUi.RunAsync(async () =>
    {
        var session = await SessionAsync();
        var (window, view) = Show(session);

        var model = Label(Button(view, "composer-model"), session.Composer.ModelLabel);
        var orchestration = Label(Button(view, "composer-orchestration"), session.Composer.OrchestrationLabel);
        // The two chips sit side by side, so their labels are one size: the body size, and never two.
        var body = Assert.IsType<double>(Resource(model, "FontSizeBase"));
        Assert.Equal(body, model.FontSize);
        Assert.Equal(orchestration.FontSize, model.FontSize);

        session.Dispose();
        window.Close();
    });

    [Fact]
    public Task The_composer_field_and_the_model_picker_rows_are_the_body_size() => SharedUi.RunAsync(async () =>
    {
        var session = await SessionAsync();
        var view = new SessionView { DataContext = session };
        // A model picker's row label: the class is what the picker's rows wear, so its resolved size is the row's size.
        var modelName = new Avalonia.Controls.TextBlock { Classes = { "model-name" }, Text = "provider/model" };
        // The price line under it: the same size, quieter only through its ink.
        var modelPrice = new Avalonia.Controls.TextBlock { Classes = { "model-price" }, Text = "$1 / $2" };
        var window = new Window
        {
            Width = 900,
            Height = 640,
            Content = new Panel { Children = { view, modelName, modelPrice } },
        };
        window.Show();
        SharedUi.Settle(window);

        var input = view.GetVisualDescendants().OfType<TextBox>()
            .Single(box => AutomationProperties.GetAutomationId(box) == "session-input");
        var body = Assert.IsType<double>(Resource(input, "FontSizeBase"));
        Assert.Equal(body, input.FontSize);
        Assert.Equal(body, modelName.FontSize);
        Assert.Equal(body, modelPrice.FontSize);

        session.Dispose();
        window.Close();
    });

    private (Window Window, SessionView View) Show(SessionWorkspaceViewModel session)
    {
        var view = new SessionView { DataContext = session };
        var window = new Window { Width = 900, Height = 640, Content = view };
        window.Show();
        SharedUi.Settle(window);
        return (window, view);
    }

    /// <summary>The one button of the composer bar carrying <paramref name="automationId"/>, after layout.</summary>
    private static Button Button(Visual view, string automationId) =>
        view.GetVisualDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetAutomationId(button) == automationId);

    /// <summary>The glyph inside a button: the bar's icon buttons hold exactly one.</summary>
    private static Geometry Icon(Button button) =>
        button.GetVisualDescendants().OfType<FluentIcon>().Single().Data!;

    /// <summary>The label a chip shows, found by its text: a chip holds the model id (or the orchestration's name) beside its icons.</summary>
    private static Avalonia.Controls.TextBlock Label(Button chip, string text) =>
        chip.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().First(block => block.Text == text);

    /// <summary>A theme resource as the buttons themselves see it (the app's dictionaries are per theme variant).</summary>
    private static object Resource(Control context, string key) =>
        context.TryFindResource(key, context.ActualThemeVariant, out var value) && value is not null
            ? value
            : throw new InvalidOperationException("no resource " + key);

    private async Task<SessionWorkspaceViewModel> SessionAsync()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "bar"), CancellationToken.None);
        var runtime = new SessionRuntime(
            new AgentRuntimeServices(
                ProviderFactory: static _ => throw new InvalidOperationException("no turn is run here"),
                HostClient: null,
                Store: store,
                Catalog: store,
                Todos: new SqliteTodoStore(directory),
                Model: new ModelSettings("test-model")),
            root);
        return new SessionWorkspaceViewModel(
            root,
            runtime,
            checkpoints: null,
            store: new SqliteSessionStore(directory),
            new ProviderServices(Path.Combine(directory, "settings.json"), directory, http));
    }
}
