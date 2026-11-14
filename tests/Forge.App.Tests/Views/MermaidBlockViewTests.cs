using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.App.Changes.Markdown;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.Tests.Git;
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

    [Fact]
    public Task The_block_takes_its_surface_header_margin_and_source_text_from_the_shared_styles() => SharedUi.RunAsync(() =>
    {
        MermaidRenderService.Current.ClearCache();
        var block = new MermaidBlockView(Flow, "mermaid", closed: false);
        var window = new Window { Width = 640, Height = 480, Content = block };
        // The markdown styles are included by the views that host blocks (ChatMarkdownView, the diff view), not by the app.
        window.Styles.Add(new StyleInclude((Uri?)null) { Source = new Uri("avares://Forge.App/Resources/MarkdownStyles.axaml") });
        window.Show();
        SharedUi.Settle(window);

        // The selectors are by type (Border.md-code, Grid.md-code-head, a ScrollViewer template): a subclass that does not
        // keep its base style key matches none of them and comes out bare - no surface, no margin, a source that never lays out.
        Assert.NotNull(block.Background);
        Assert.Equal(new Thickness(10, 8), block.Padding);
        var header = block.GetVisualDescendants().OfType<MarkdownCodeHeader>().Single();
        Assert.Equal(new Thickness(0, 0, 0, 4), header.Margin);
        var text = block.GetVisualDescendants().OfType<SelectableTextBlock>().Single(item => item.Classes.Contains("md-code-text"));
        Assert.Equal(Flow, text.Text);
        Assert.True(text.Bounds.Width > 0 && text.Bounds.Height > 0, "the source text did not lay out");
        Assert.True(Source(block).Bounds.Height > 0);

        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_timeout_names_the_timeout_this_request_used_in_milliseconds() => SharedUi.RunAsync(async () =>
    {
        var service = new MermaidRenderService { Renderer = static _ => MermaidRenderResult.Failed(MermaidRenderFailure.Timeout) };
        await WithAsync(service, async () =>
        {
            try
            {
                // 2500 ms has a zero millisecond component, which is what a TimeSpan.Milliseconds read would have shown.
                MermaidSettings.TimeoutMilliseconds = static () => 2500;
                var block = new MermaidBlockView(Flow, "mermaid", closed: true);
                var window = Show(block);
                Assert.True(await UntilAsync(window, () => Status(block).Classes.Contains("mermaid-status-error")));

                var expected = Loc.Instance.Format(MermaidStrings.Timeout, TextFormat.Number(2500));
                Assert.Equal(expected, Status(block).Text);
                Assert.NotEqual(Loc.Instance.Format(MermaidStrings.Timeout, TextFormat.Number(0)), Status(block).Text);

                // The setting changing afterwards does not rewrite what the failed request said.
                MermaidSettings.TimeoutMilliseconds = static () => 9000;
                block.Update(Flow, "mermaid", closed: true);
                window.Close();
            }
            finally
            {
                MermaidSettings.Reset();
            }
        });
    });

    [Fact]
    public Task A_source_over_the_size_limit_says_the_diagram_is_too_large() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        var block = new MermaidBlockView("flowchart TD\n" + new string('x', MermaidRenderService.MaxSourceLength), "mermaid", closed: true);
        var window = Show(block);

        Assert.True(await UntilAsync(window, () => Status(block).Classes.Contains("mermaid-status-error")));
        Assert.Equal(Loc.Instance[MermaidStrings.TooLarge], Status(block).Text);
        Assert.True(Find<Button>(block, "mermaid-retry").IsVisible);
        Assert.True(Source(block).IsVisible);
        window.Close();
    });

    [Fact]
    public Task A_type_mermaider_rejects_as_not_allowed_stays_a_plain_code_block() => SharedUi.RunAsync(async () =>
    {
        // The sniffing let the source through (it is enabled), and the renderer answers "not allowed": no error panel at all.
        var service = new MermaidRenderService { Renderer = static _ => MermaidRenderResult.Failed(MermaidRenderFailure.Disabled) };
        await WithAsync(service, async () =>
        {
            var block = new MermaidBlockView(Flow, "mermaid", closed: true);
            var window = Show(block);

            await Task.Delay(300);
            Assert.True(await UntilAsync(window, () => !StatusRow(block).IsVisible));
            Assert.DoesNotContain("mermaid-status-error", Status(block).Classes);
            Assert.False(Find<Button>(block, "mermaid-retry").IsEffectivelyVisible);
            Assert.False(Find<Button>(block, "mermaid-toggle-source").IsVisible);
            Assert.True(Source(block).IsVisible);
            Assert.Null(Diagram(block).Picture);
            window.Close();
        });
    });

    [Fact]
    public Task The_retry_button_renders_again_and_a_fixed_renderer_shows_the_diagram() => SharedUi.RunAsync(async () =>
    {
        var calls = 0;
        var service = new MermaidRenderService
        {
            Renderer = request => Interlocked.Increment(ref calls) == 1
                ? MermaidRenderResult.Failed(MermaidRenderFailure.Parse, "transient failure")
                : MermaidRenderService.RenderCore(request),
        };
        await WithAsync(service, async () =>
        {
            var block = new MermaidBlockView(Flow, "mermaid", closed: true);
            var window = Show(block);
            Assert.True(await UntilAsync(window, () => Status(block).Classes.Contains("mermaid-status-error")));
            Assert.Contains("transient failure", Detail(block).Text, StringComparison.Ordinal);

            Find<Button>(block, "mermaid-retry").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(await UntilAsync(window, () => Diagram(block).Picture is not null));
            Assert.Equal(2, calls);
            Assert.DoesNotContain("mermaid-status-error", Status(block).Classes);
            Assert.True(Diagram(block).IsVisible);
            window.Close();
        });
    });

    [Fact]
    public Task A_queued_hand_off_says_it_went_to_the_prompt_box() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        try
        {
            MermaidFixHost.ResolveOverride = _ => new FakeMessageHost { Ran = false };
            var block = new MermaidBlockView(NotMermaid, "mermaid", closed: true);
            var window = Show(block);
            Assert.True(await UntilAsync(window, () => Status(block).Classes.Contains("mermaid-status-error")));

            Find<Button>(block, "mermaid-fix").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(await UntilAsync(window, () => FixStatus(block).Text == Loc.Instance[MermaidStrings.FixQueued]));
            Assert.True(Find<Button>(block, "mermaid-fix").IsEnabled);
            window.Close();
        }
        finally
        {
            MermaidFixHost.ResolveOverride = null;
        }
    });

    [Fact]
    public Task A_hand_off_that_throws_is_reported_and_the_buttons_come_back() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        try
        {
            MermaidFixHost.ResolveOverride = _ => new FakeMessageHost { Failure = new InvalidOperationException("session gone") };
            var block = new MermaidBlockView(NotMermaid, "mermaid", closed: true);
            var window = Show(block);
            Assert.True(await UntilAsync(window, () => Status(block).Classes.Contains("mermaid-status-error")));

            Find<Button>(block, "mermaid-fix").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(await UntilAsync(window, () => FixStatus(block).Text == Loc.Instance[MermaidStrings.RenderFailed]));
            Assert.True(Find<Button>(block, "mermaid-fix").IsEnabled);
            Assert.True(Find<Button>(block, "mermaid-retry").IsEnabled);
            window.Close();
        }
        finally
        {
            MermaidFixHost.ResolveOverride = null;
        }
    });

    [Fact]
    public Task A_theme_switched_while_the_block_was_detached_renders_the_new_palette_on_reattach() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        var block = new MermaidBlockView(Flow, "mermaid", closed: true);
        var window = new Window { Width = 640, Height = 480, Content = block, RequestedThemeVariant = ThemeVariant.Light };
        window.Show();
        SharedUi.Settle(window);
        Assert.True(await UntilAsync(window, () => Diagram(block).Picture is not null));
        var light = Diagram(block).Picture;

        window.Content = null;
        window.RequestedThemeVariant = ThemeVariant.Dark;
        SharedUi.Settle(window);
        window.Content = block;
        SharedUi.Settle(window);

        Assert.True(await UntilAsync(window, () => !ReferenceEquals(Diagram(block).Picture, light)));
        Assert.True(Diagram(block).IsVisible);
        Assert.DoesNotContain("mermaid-status-error", Status(block).Classes);
        window.Close();
    });

    [Fact]
    public Task Reattaching_under_an_unchanged_theme_keeps_the_picture() => SharedUi.RunAsync(async () =>
    {
        MermaidRenderService.Current.ClearCache();
        var block = new MermaidBlockView(Flow, "mermaid", closed: true);
        var window = Show(block);
        Assert.True(await UntilAsync(window, () => Diagram(block).Picture is not null));
        var picture = Diagram(block).Picture;

        window.Content = null;
        SharedUi.Settle(window);
        window.Content = block;
        SharedUi.Settle(window);
        await Task.Delay(50);
        SharedUi.Settle(window);

        Assert.Same(picture, Diagram(block).Picture);
        window.Close();
    });

    [Fact]
    public Task A_block_detached_while_it_renders_drops_the_result_and_renders_again_on_reattach() => SharedUi.RunAsync(async () =>
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new MermaidRenderService
        {
            Renderer = request =>
            {
                started.TrySetResult();
                release.Wait();
                return MermaidRenderService.RenderCore(request);
            },
        };
        await WithAsync(service, async () =>
        {
            var block = new MermaidBlockView(Flow, "mermaid", closed: true);
            var window = Show(block);
            await started.Task;

            window.Content = null;
            SharedUi.Settle(window);
            release.Set();
            await Task.Delay(300);
            SharedUi.Settle(window);

            // The cancelled wait never applied its result to the detached block.
            Assert.Null(Diagram(block).Picture);

            window.Content = block;
            SharedUi.Settle(window);
            Assert.True(await UntilAsync(window, () => Diagram(block).Picture is not null));
            window.Close();
        });
    });

    [Fact]
    public Task Resolving_the_host_in_a_real_tree_never_falls_back_to_the_active_session_from_a_session_view() => SharedUi.RunAsync(async () =>
    {
        var directory = TestTempFiles.NewDirectory("forge-mermaid-host-");
        try
        {
            await using var harness = await GitAppHarness.OpenWithSession(directory);
            var active = harness.ViewModel.ActiveSession!;
            Assert.True(active.CanHandOff);

            // A block in a document preview (no SessionView above it) is handed to the window's active session.
            var preview = new MermaidBlockView(Flow, "mermaid", closed: false);
            var previewWindow = new Window { Width = 400, Height = 300, Content = preview, DataContext = harness.ViewModel };
            previewWindow.Show();
            SharedUi.Settle(previewWindow);
            Assert.Same(active, MermaidFixHost.Resolve(preview));
            previewWindow.Close();

            // A block in a session's own transcript that cannot take the hand-off has no host: it must not leak the message
            // into whichever session happens to be active.
            var closedHost = new FakeMessageHost { CanHandOff = false };
            var inClosed = new MermaidBlockView(Flow, "mermaid", closed: false);
            var closedWindow = new Window
            {
                Width = 400,
                Height = 300,
                DataContext = harness.ViewModel,
                Content = new SessionView { DataContext = closedHost, Content = inClosed },
            };
            closedWindow.Show();
            SharedUi.Settle(closedWindow);
            Assert.Null(MermaidFixHost.Resolve(inClosed));
            closedWindow.Close();

            // The nearest session view that can take it is the host, whatever is active.
            var openHost = new FakeMessageHost();
            var inOpen = new MermaidBlockView(Flow, "mermaid", closed: false);
            var openWindow = new Window
            {
                Width = 400,
                Height = 300,
                DataContext = harness.ViewModel,
                Content = new SessionView { DataContext = openHost, Content = inOpen },
            };
            openWindow.Show();
            SharedUi.Settle(openWindow);
            Assert.Same(openHost, MermaidFixHost.Resolve(inOpen));
            openWindow.Close();
        }
        finally
        {
            TestTempFiles.DeleteTree(directory);
        }
    });

    [Fact]
    public Task Repeated_updates_of_an_open_fence_only_replace_the_text() => SharedUi.RunAsync(() =>
    {
        MermaidRenderService.Current.ClearCache();
        var block = new MermaidBlockView(Flow, "mermaid", closed: false);
        var window = Show(block);

        var grown = Flow;
        for (var frame = 0; frame < 5; frame++)
        {
            grown += "  C" + frame + " --> D" + frame + "\n";
            block.Update(grown, "mermaid", closed: false);
        }

        Assert.Equal(grown, Source(block).Text);
        Assert.Equal(grown, block.CopyText);
        Assert.True(Source(block).IsVisible);
        Assert.False(StatusRow(block).IsVisible);
        Assert.Null(Diagram(block).Picture);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_streaming_frame_with_unchanged_state_allocates_far_less_than_a_state_pass() => SharedUi.RunAsync(() =>
    {
        MermaidRenderService.Current.ClearCache();
        var block = new MermaidBlockView(Flow, "mermaid", closed: false);
        var window = Show(block);
        var texts = new string[64];
        for (var i = 0; i < texts.Length; i++)
        {
            texts[i] = Flow + new string('x', i);
        }

        for (var warm = 0; warm < 3; warm++)
        {
            block.Update(texts[warm], "mermaid", closed: false);
        }

        const int frames = 2000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < frames; frame++)
        {
            block.Update(texts[frame % texts.Length], "mermaid", closed: false);
        }

        var perFrame = (GC.GetAllocatedBytesForCurrentThread() - before) / frames;
        Console.WriteLine($"mermaid open-fence Update: {perFrame} bytes per frame");
        // A full state pass re-binds the status glyph, its brush (and the toggle icon) every frame - several KB. The text
        // swap itself allocates only what Avalonia's property change costs.
        Assert.True(perFrame < 1024, $"an unchanged streaming frame allocated {perFrame} bytes");
        window.Close();
        return Task.CompletedTask;
    });

    /// <summary>Swaps the service the blocks use for the duration of <paramref name="body"/>, so a test controls the renderer.</summary>
    private static async Task WithAsync(MermaidRenderService service, Func<Task> body)
    {
        var previous = MermaidRenderService.Current;
        MermaidRenderService.Current = service;
        try
        {
            await body();
        }
        finally
        {
            MermaidRenderService.Current = previous;
        }
    }

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

        /// <summary>What the hand-off reports: true when a turn ran, false when the message was queued in the prompt box.</summary>
        public bool Ran { get; init; } = true;

        /// <summary>When set, the hand-off throws it instead of returning.</summary>
        public Exception? Failure { get; init; }

        public string? Message { get; private set; }

        public LocText Origin { get; private set; }

        public Task<bool> HandMessageAsync(string message, LocText origin)
        {
            Message = message;
            Origin = origin;
            return Failure is { } failure ? Task.FromException<bool>(failure) : Task.FromResult(Ran);
        }
    }
}
