using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Forge.App.Changes;
using Forge.App.Changes.Markdown;
using Forge.App.Localization;
using Forge.App.Review;
using Forge.App.Terminal;
using Forge.App.Tests.Changes;
using Forge.App.Tests.Editor;
using Forge.App.Tests.Terminal;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Localization;

/// <summary>
/// The creating-thread rule of <see cref="LanguageThreadGuardTests"/> for the subscribers that are Avalonia controls rather
/// than view models (App SPEC 61). A control subscribes when it is attached and unsubscribes when it leaves, but a window
/// whose session is gone (a previous headless session) stays in the process-wide table: a switch raised on another thread
/// belongs to it and must be ignored, because touching the control from there fails <c>VerifyAccess</c> and queues a layout
/// pass on the wrong dispatcher.
/// <para>Each case puts a sentinel where its handler writes and raises that one handler through
/// <see cref="LanguageSubscriptions"/> — from a thread-pool thread first (the sentinel must survive) and then from the
/// attaching thread (the sentinel must be replaced by the localized value), so a guard that is too wide, one that stops the
/// control following a real switch, fails here as well. The language itself is never switched: doing so would reach every
/// other subscriber in the process. The controls covered are the ones whose handler writes something a test can hold: a
/// terminal surface and a timeline gutter their read-screen name, a code block header and a review overlay their button's
/// name, a mermaid block its diagram's name, a diff editor the file header it recycles.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class ControlLanguageThreadGuardTests
{
    /// <summary>What a handler is expected to overwrite; it is not a value any string table holds.</summary>
    private const string Sentinel = "stale";

    [Fact]
    public async Task A_terminal_surface_ignores_a_switch_from_another_thread_and_follows_one_from_its_own()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var scene = TerminalSceneUi.Open(new FakeTerminalHost());
                try
                {
                    // The handler writes the surface's read-screen name, so that is the sentinel's seat.
                    AutomationProperties.SetName(scene.Control, Sentinel);

                    await AssertIgnoredOffThreadAsync(scene.Control);
                    Assert.Equal(Sentinel, AutomationProperties.GetName(scene.Control));

                    Assert.Equal(1, LanguageSubscriptions.RaiseHere(scene.Control));
                    Assert.Equal(
                        Loc.Instance[TerminalStrings.AutomationSurface],
                        AutomationProperties.GetName(scene.Control));
                }
                finally
                {
                    scene.Window.Close();
                    await scene.ViewModel.DisposeAsync();
                }
            });
    }

    [Fact]
    public async Task A_code_block_header_ignores_a_switch_from_another_thread_and_follows_one_from_its_own()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var header = new MarkdownCodeHeader(new CopyableText());
                var window = SharedUi.Show(header, 400, 200);
                try
                {
                    var copy = header
                        .GetVisualDescendants()
                        .OfType<Button>()
                        .Single(button => AutomationProperties.GetAutomationId(button) == "md-code-copy");

                    // The header localizes its copy button, not itself, so the sentinel sits on the button.
                    AutomationProperties.SetName(copy, Sentinel);

                    await AssertIgnoredOffThreadAsync(header);
                    Assert.Equal(Sentinel, AutomationProperties.GetName(copy));

                    Assert.Equal(1, LanguageSubscriptions.RaiseHere(header));
                    Assert.Equal(Loc.Instance[MarkdownStrings.CopyCode], AutomationProperties.GetName(copy));
                }
                finally
                {
                    window.Close();
                }
            });
    }

    [Fact]
    public async Task A_file_document_view_ignores_a_switch_from_another_thread_and_follows_one_from_its_own()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = FileDocumentViewModel.Create();
                var host = EditorUi.Show(model);
                try
                {
                    // The view holds the subscription and hands the refresh to the document, so the document's own
                    // notifications are what a binding would see.
                    var raised = PropertyChangeLog.For(model);

                    await AssertIgnoredOffThreadAsync(host.View);
                    Assert.DoesNotContain(nameof(FileDocumentViewModel.TabTitle), raised);

                    Assert.Equal(1, LanguageSubscriptions.RaiseHere(host.View));
                    Assert.Contains(nameof(FileDocumentViewModel.TabTitle), raised);
                }
                finally
                {
                    host.Window.Close();
                }
            });
    }

    [Fact]
    public async Task A_diff_editor_ignores_a_switch_from_another_thread_and_follows_one_from_its_own()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var files = DiffFixtures.Files(1);
                var view = new DiffEditorView { Files = files };
                var window = SharedUi.Show(view, 900, 600);
                try
                {
                    SharedUi.Settle(window);
                    view.SettleNow();
                    SharedUi.Settle(window);
                    var header = view.ActiveOverlayControls.OfType<DiffFileHeader>().Single();

                    // A switch recycles every overlay row, and the row that comes back is named again: that name is the seat.
                    AutomationProperties.SetName(header, Sentinel);

                    await AssertIgnoredOffThreadAsync(view);
                    Assert.Equal(Sentinel, AutomationProperties.GetName(header));

                    Assert.Equal(1, LanguageSubscriptions.RaiseHere(view));
                    Assert.Equal(
                        Loc.Instance.Format(DiffStrings.FileHeader, files[0].Path),
                        AutomationProperties.GetName(view.ActiveOverlayControls.OfType<DiffFileHeader>().Single()));
                }
                finally
                {
                    window.Close();
                }
            });
    }

    [Fact]
    public async Task A_mermaid_block_ignores_a_switch_from_another_thread_and_follows_one_from_its_own()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                // An open fence renders nothing, so the block stays in the one state whose only localized text is its
                // diagram's read-screen name: no renderer, no cache and no render tick between the two raises.
                var block = new MermaidBlockView("flowchart TD\n  A[Start] --> B{Done}\n", "mermaid", closed: false);
                var window = SharedUi.Show(block, 600, 400);
                try
                {
                    var diagram = block.GetLogicalDescendants().OfType<MermaidDiagramView>().Single();
                    AutomationProperties.SetName(diagram, Sentinel);

                    await AssertIgnoredOffThreadAsync(block);
                    Assert.Equal(Sentinel, AutomationProperties.GetName(diagram));

                    Assert.Equal(1, LanguageSubscriptions.RaiseHere(block));
                    Assert.Equal(Loc.Instance[MermaidStrings.Diagram], AutomationProperties.GetName(diagram));
                }
                finally
                {
                    window.Close();
                }
            });
    }

    [Fact]
    public async Task A_timeline_gutter_ignores_a_switch_from_another_thread_and_follows_one_from_its_own()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var transcript = new TranscriptViewModel(collapseOlderTurns: false);
                transcript.AddUserMessage("hello", new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
                var list = new ListBox { ItemsSource = transcript.Items };
                var window = SharedUi.Show(list, 900, 400);
                try
                {
                    SharedUi.Settle(window);
                    var gutter = window.GetVisualDescendants().OfType<TimelineGutter>().First();
                    AutomationProperties.SetName(gutter, Sentinel);

                    await AssertIgnoredOffThreadAsync(gutter);
                    Assert.Equal(Sentinel, gutter.NodeName);

                    // The name is forced on, so a gutter whose node did not change still writes it again after a switch.
                    Assert.Equal(1, LanguageSubscriptions.RaiseHere(gutter));
                    Assert.Equal(
                        TimelineStrings.Describe(TimelineNodeKind.User, TimelineNodeState.Idle),
                        gutter.NodeName);
                }
                finally
                {
                    window.Close();
                }
            });
    }

    [Fact]
    public async Task A_review_overlay_ignores_a_switch_from_another_thread_and_follows_one_from_its_own()
    {
        await SharedUi.RunAsync(
            async () =>
            {
                var model = FileDocumentViewModel.Create();
                var host = EditorUi.Show(model);
                try
                {
                    var overlay = Assert.IsType<ReviewOverlay>(host.View.Review);
                    var icon = host
                        .Window
                        .GetVisualDescendants()
                        .OfType<Button>()
                        .Single(button => AutomationProperties.GetAutomationId(button) == "review-add");

                    // The overlay localizes its "add a comment" icon rather than itself, so the sentinel sits on the icon.
                    AutomationProperties.SetName(icon, Sentinel);

                    await AssertIgnoredOffThreadAsync(overlay);
                    Assert.Equal(Sentinel, AutomationProperties.GetName(icon));

                    Assert.Equal(1, LanguageSubscriptions.RaiseHere(overlay));
                    Assert.Equal(Loc.Instance[ReviewStrings.AddComment], AutomationProperties.GetName(icon));
                }
                finally
                {
                    host.Window.Close();
                }
            });
    }

    /// <summary>
    /// Raises <paramref name="subscriber"/>'s own handler from a thread-pool thread and pins that exactly one handler ran,
    /// off the thread the control was attached on. An unguarded control throws here rather than reaching the assertion after
    /// it, which is the point of the case.
    /// </summary>
    private static async Task AssertIgnoredOffThreadAsync(object subscriber)
    {
        var attachingThread = Environment.CurrentManagedThreadId;
        var (handlers, raisedOn) = await LanguageSubscriptions.RaiseOnPoolThreadAsync(subscriber);
        Assert.Equal(1, handlers);
        Assert.NotEqual(attachingThread, raisedOn);
    }

    /// <summary>The least a code block header will take: the copy button reads this when pressed.</summary>
    private sealed class CopyableText : ICopyableText
    {
        public string CopyText => "copy me";
    }
}
