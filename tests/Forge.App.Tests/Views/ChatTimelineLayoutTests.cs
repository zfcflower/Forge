using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.Tests.ViewModels;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// The chat region's insets and message width (forge-app spec "聊天时间轴(W13)"): the transcript, the composer card and the
/// automation banner share one 16 DIP window inset from the same three tokens, and a user bubble fills the width instead of
/// a fixed maximum. Rendered by the real <see cref="SessionView"/> over a real workspace view model.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ChatTimelineLayoutTests
{
    private const string SessionId = "chat-insets";
    private static readonly DateTimeOffset At = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Transcript_composer_and_banner_share_one_window_inset()
    {
        await SharedUi.RunAsync(() =>
        {
            using var rig = SessionRig.Show();
            var window = rig.Window;

            var transcript = rig.ByName<ListBox>("Transcript");
            var transcriptInset = (Thickness)transcript.FindResource("ChatTranscriptPadding")!;
            Assert.Equal(transcriptInset.Left, transcript.Padding.Left);
            Assert.Equal(transcriptInset.Right, transcript.Padding.Right);
            Assert.Equal(16, transcript.Padding.Left);
            Assert.Equal(16, transcript.Padding.Right);

            var banner = rig.ByName<Border>("AutomationBanner");
            var bannerInset = (Thickness)banner.FindResource("ChatBannerMargin")!;
            Assert.Equal(bannerInset.Left, banner.Margin.Left);
            Assert.Equal(bannerInset.Right, banner.Margin.Right);
            Assert.Equal(16, banner.Margin.Left);
            Assert.Equal(16, banner.Margin.Right);

            // The composer card's host grid carries the composer margin; it must agree with the other two edges.
            var composerHost = Assert.IsType<Grid>(rig.ByName<Border>("ComposerCard").GetVisualParent());
            var composerInset = (Thickness)composerHost.FindResource("ChatComposerMargin")!;
            Assert.Equal(composerInset.Left, composerHost.Margin.Left);
            Assert.Equal(composerInset.Right, composerHost.Margin.Right);
            Assert.Equal(16, composerHost.Margin.Left);
            Assert.Equal(16, composerHost.Margin.Right);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_user_bubble_has_no_fixed_maximum_width()
    {
        await SharedUi.RunAsync(() =>
        {
            using var rig = SessionRig.Show();
            var bubble = rig.Window.GetVisualDescendants()
                .OfType<Border>()
                .Single(border => border.Classes.Contains("user-bubble"));
            Assert.True(
                double.IsPositiveInfinity(bubble.MaxWidth),
                $"the user bubble must fill the window: MaxWidth was {bubble.MaxWidth}");
            return Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ harness

    /// <summary>
    /// The real <see cref="SessionView"/> over a real <see cref="SessionWorkspaceViewModel"/> with one user message in the
    /// transcript. Same arrangement as the attachment suite minus the model catalogue, which the insets do not read.
    /// </summary>
    private sealed class SessionRig : IDisposable
    {
        private readonly ComposerSuggestionTests.TestServices services;
        private readonly SessionWorkspaceViewModel workspace;
        private readonly string root;

        private SessionRig(
            ComposerSuggestionTests.TestServices services,
            SessionWorkspaceViewModel workspace,
            Window window,
            string root)
        {
            this.services = services;
            this.workspace = workspace;
            Window = window;
            this.root = root;
        }

        public Window Window { get; }

        public static SessionRig Show()
        {
            var root = Path.Combine(Path.GetTempPath(), "forge-chat-insets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var services = ComposerSuggestionTests.TestServices.Create(root);
            var info = new SessionInfo(SessionId, root, "insets", At);
            var store = new EmptySessionStore(At);
            var workspace = new SessionWorkspaceViewModel(
                info,
                new SessionRuntime(
                    new AgentRuntimeServices(
                        static _ => new MissingCredentialProvider("unconfigured", ProviderStrings.SectionMissingText()),
                        HostClient: null,
                        store,
                        new EmptySessionCatalog(At),
                        new EmptyTodoStore("todo-1", At),
                        new ModelSettings("model")),
                    info),
                checkpoints: null,
                store,
                services.Services,
                composerPreferences: new ComposerPreferences(),
                fileSource: static _ => Task.FromResult<IReadOnlyList<string>>([]),
                skillSource: static () => []);
            workspace.Transcript.AddUserMessage("hello", At);

            var window = new Window { Width = 1200, Height = 800, Content = new SessionView { DataContext = workspace } };
            window.Show();
            SharedUi.Settle(window);
            return new SessionRig(services, workspace, window, root);
        }

        public T ByName<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
            ?? throw new InvalidOperationException("no " + typeof(T).Name + " named '" + name + "'");

        public void Dispose()
        {
            Window.Close();
            workspace.Dispose();
            services.Dispose();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
