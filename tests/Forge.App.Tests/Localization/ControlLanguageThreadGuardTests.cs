using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.Changes.Markdown;
using Forge.App.Localization;
using Forge.App.Terminal;
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
/// other subscriber in the process.</para>
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
