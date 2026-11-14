using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Forge.App.Changes.Markdown;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// Pins the fenced Mermaid block (wave W11): an open fence shows its source and renders nothing, a closed one renders once and
/// flips to the diagram with a source/diagram toggle, a type outside the whitelist keeps the plain code-block chrome without
/// an error, and a failed render keeps the source visible with the renderer's own message, a retry and - when a session can
/// take it - the hand-off to the model.
/// <para>The block renders on a background thread, so every "it rendered" assertion polls the dispatcher instead of sleeping.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class MermaidBlockViewTests
{
    private const string Flow = "flowchart TD\n  A[Start] --> B{Done}\n";
    private const string NotMermaid = "this is not mermaid at all";

    [Fact]
    public Task An_unclosed_fence_shows_the_source_and_draws_nothing() => SharedUi.RunAsync(() =>
    {
        MermaidRenderService.Current.ClearCache();
        var block = new MermaidBlockView(Flow, "mermaid", closed: false);
        var window = Show(block);

        Assert.Null(Diagram(block).Picture);
        Assert.True(Source(block).IsVisible);
        Assert.Equal(Flow, Source(block).Text);
        Assert.False(StatusRow(block).IsVisible);
        Assert.False(Find<Button>(block, "mermaid-toggle-source").IsVisible);
        Assert.Equal(Flow, block.CopyText);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Closing_the_fence_renders_the_diagram_and_hides_the_source() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        var block = new MermaidBlockView(Flow, "mermaid", closed: false);
        var window = Show(block);
        Assert.Null(Diagram(block).Picture);

        block.Update(Flow, "mermaid", closed: true);

        Assert.True(await UntilAsync(window, () => Diagram(block).Picture is not null));
        Assert.False(Source(block).IsVisible);
        Assert.True(Diagram(block).IsVisible);
        Assert.True(Find<Button>(block, "mermaid-toggle-source").IsVisible);
        window.Close();
    });

    [Fact]
    public Task A_type_outside_the_whitelist_stays_a_plain_code_block() => SharedUi.RunAsync(() =>
    {
        MermaidRenderService.Current.ClearCache();
        try
        {
            MermaidSettings.DiagramTypes = static () => "pie";
            var block = new MermaidBlockView(Flow, "mermaid", closed: true);
            var window = Show(block);

            // The whitelist is a display decision, not a failure: no picture, no status line, no toggle, the source stays.
            Assert.Null(Diagram(block).Picture);
            Assert.False(StatusRow(block).IsVisible);
            Assert.False(Find<Button>(block, "mermaid-toggle-source").IsVisible);
            Assert.True(Source(block).IsVisible);
            window.Close();
        }
        finally
        {
            MermaidSettings.Reset();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task A_failed_render_keeps_the_source_and_shows_the_reason() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        var block = new MermaidBlockView(NotMermaid, "mermaid", closed: true);
        var window = Show(block);

        Assert.True(await UntilAsync(window, () => Status(block).Classes.Contains("mermaid-status-error")));

        Assert.False(string.IsNullOrWhiteSpace(Status(block).Text));
        var detail = Detail(block);
        Assert.True(detail.IsVisible);
        Assert.Contains("Invalid mermaid header", detail.Text, StringComparison.Ordinal);
        Assert.True(Find<Button>(block, "mermaid-retry").IsVisible);
        // No session hosts this block, so nothing can take the hand-off.
        Assert.False(Find<Button>(block, "mermaid-fix").IsVisible);
        Assert.True(Source(block).IsVisible);
        Assert.Null(Diagram(block).Picture);
        window.Close();
    });

    [Fact]
    public Task A_failure_becomes_a_diagram_when_the_source_is_fixed() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        var block = new MermaidBlockView(NotMermaid, "mermaid", closed: true);
        var window = Show(block);
        Assert.True(await UntilAsync(window, () => Status(block).Classes.Contains("mermaid-status-error")));

        block.Update(Flow, "mermaid", closed: true);

        Assert.True(await UntilAsync(window, () => Diagram(block).Picture is not null));
        Assert.DoesNotContain("mermaid-status-error", Status(block).Classes);
        Assert.True(Diagram(block).IsVisible);
        Assert.False(Source(block).IsVisible);
        window.Close();
    });

    [Fact]
    public Task The_toggle_switches_between_the_diagram_and_its_source() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        var block = new MermaidBlockView(Flow, "mermaid", closed: true);
        var window = Show(block);
        Assert.True(await UntilAsync(window, () => Diagram(block).Picture is not null));
        var toggle = Find<Button>(block, "mermaid-toggle-source");

        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(Source(block).IsVisible);
        Assert.False(Diagram(block).IsVisible);
        Assert.Equal(Loc.Instance[MermaidStrings.ShowDiagram], AutomationProperties.GetName(toggle));

        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(Diagram(block).IsVisible);
        Assert.False(Source(block).IsVisible);
        Assert.Equal(Loc.Instance[MermaidStrings.ShowSource], AutomationProperties.GetName(toggle));
        window.Close();
    });

    [Fact]
    public Task The_fix_button_hands_the_error_and_the_source_to_the_session() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        var host = new FakeMessageHost();
        try
        {
            MermaidFixHost.ResolveOverride = _ => host;
            var block = new MermaidBlockView(NotMermaid, "mermaid", closed: true);
            var window = Show(block);
            Assert.True(await UntilAsync(window, () => Status(block).Classes.Contains("mermaid-status-error")));

            var fix = Find<Button>(block, "mermaid-fix");
            Assert.True(fix.IsVisible);
            fix.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(await UntilAsync(
                window,
                () => FixStatus(block).Text == Loc.Instance[MermaidStrings.FixSent]));
            Assert.Contains("Invalid mermaid header", host.Message, StringComparison.Ordinal);
            Assert.Contains(NotMermaid, host.Message, StringComparison.Ordinal);
            // The turn is labelled as a Mermaid fix, so the transcript says where the request came from.
            Assert.Equal(Loc.Instance[MermaidStrings.FixOrigin], host.Origin.Resolve());
            window.Close();
        }
        finally
        {
            MermaidFixHost.ResolveOverride = null;
        }
    });

    [Fact]
    public Task The_fix_button_is_hidden_when_the_session_cannot_take_the_message() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        try
        {
            MermaidFixHost.ResolveOverride = _ => new FakeMessageHost { CanHandOff = false };
            var block = new MermaidBlockView(NotMermaid, "mermaid", closed: true);
            var window = Show(block);
            Assert.True(await UntilAsync(window, () => Status(block).Classes.Contains("mermaid-status-error")));

            Assert.False(Find<Button>(block, "mermaid-fix").IsVisible);
            Assert.True(Find<Button>(block, "mermaid-retry").IsVisible);
            window.Close();
        }
        finally
        {
            MermaidFixHost.ResolveOverride = null;
        }
    });

    [Fact]
    public Task A_theme_switch_renders_the_diagram_again_with_the_new_palette() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        var block = new MermaidBlockView(Flow, "mermaid", closed: true);
        var window = new Window
        {
            Width = 640,
            Height = 480,
            Content = block,
            RequestedThemeVariant = ThemeVariant.Light,
        };
        window.Show();
        SharedUi.Settle(window);
        Assert.True(await UntilAsync(window, () => Diagram(block).Picture is not null));
        var light = Diagram(block).Picture;

        window.RequestedThemeVariant = ThemeVariant.Dark;
        SharedUi.Settle(window);

        Assert.True(await UntilAsync(window, () => !ReferenceEquals(Diagram(block).Picture, light)));
        // The new picture replaces the old one in place: the block never fell back to the source or to a failure.
        Assert.True(Diagram(block).IsVisible);
        Assert.False(Source(block).IsVisible);
        Assert.DoesNotContain("mermaid-status-error", Status(block).Classes);
        window.Close();
    });

    [Fact]
    public Task A_rendered_diagram_and_its_block_carry_accessible_names() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        var block = new MermaidBlockView(Flow, "mermaid", closed: true);
        var window = Show(block);
        Assert.True(await UntilAsync(window, () => Diagram(block).Picture is not null));

        Assert.Equal("mermaid-diagram", AutomationProperties.GetAutomationId(Diagram(block)));
        Assert.Equal(Loc.Instance[MermaidStrings.Diagram], AutomationProperties.GetName(Diagram(block)));
        Assert.Equal("mermaid", AutomationProperties.GetName(block));
        Assert.Equal("mermaid-toggle-source", AutomationProperties.GetAutomationId(Find<Button>(block, "mermaid-toggle-source")));
        window.Close();
    });

    private static Window Show(MermaidBlockView block)
    {
        var window = SharedUi.Show(block, 640, 480);
        SharedUi.Settle(window);
        return window;
    }

    /// <summary>Pumps the dispatcher and the render timer until <paramref name="condition"/> holds (the render is a background task).</summary>
    private static async Task<bool> UntilAsync(Window window, Func<bool> condition, int iterations = 400)
    {
        for (var attempt = 0; attempt < iterations; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition())
            {
                return true;
            }

            await Task.Delay(5, CancellationToken.None);
        }

        return condition();
    }

    private static T Find<T>(Control root, string automationId)
        where T : Control =>
        root.GetLogicalDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == automationId);

    private static MermaidDiagramView Diagram(Control block) => block.GetLogicalDescendants().OfType<MermaidDiagramView>().Single();

    private static MarkdownCodeTextView Source(Control block) => block.GetLogicalDescendants().OfType<MarkdownCodeTextView>().Single();

    private static StackPanel StatusRow(Control block) => Find<StackPanel>(block, "mermaid-status");

    private static TextBlock Status(Control block) =>
        block.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("mermaid-status"));

    private static SelectableTextBlock Detail(Control block) =>
        block.GetLogicalDescendants().OfType<SelectableTextBlock>().Single(text => text.Classes.Contains("mermaid-detail"));

    private static TextBlock FixStatus(Control block) =>
        block.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("mermaid-fix-status"));

    /// <summary>The session a chat reply would provide; records what the block handed it.</summary>
    private sealed class FakeMessageHost : IAgentMessageHost
    {
        public bool CanHandOff { get; init; } = true;

        public string? Message { get; private set; }

        public LocText Origin { get; private set; }

        public Task<bool> HandMessageAsync(string message, LocText origin)
        {
            Message = message;
            Origin = origin;
            return Task.FromResult(true);
        }
    }
}
