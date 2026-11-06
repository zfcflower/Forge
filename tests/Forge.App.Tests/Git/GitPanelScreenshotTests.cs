using System.Collections.Concurrent;
using System.Text;
using Avalonia.Headless;
using Forge.App.Git;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// The Git panel's "Screenshots" button (forge-app Git tab): the command the project declared is on screen before the
/// click, the button is only offered when there is a pull request and a declaration, and a click runs the same
/// <see cref="ScreenshotRunner"/> as <c>pr_screenshots</c> - capture in the worktree, capture of the base in a temporary
/// worktree, publish to the assets branch and the <c>## Screenshots</c> section - reporting a failure with the hint of its
/// failure kind. No approval card is involved: the click is the confirmation, which is why the command is shown next to it.
/// </summary>
[Collection("forge-app-ui")]
public sealed class GitPanelScreenshotTests
{
    [Fact]
    public async Task The_declared_command_is_shown_and_the_click_publishes_the_screenshots()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        ScreenshotTestRig.DeclareScreenshots(sandbox, work);
        ScreenshotTestRig.PointOriginAtGitHub(sandbox, work);

        var platform = new ScreenshotFakePlatform { Current = ScreenshotTestRig.PullRequest() };
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await AttachAndWaitForPullRequestAsync(session, panel, work);

            var before = await session.Dispatch(
                () => new
                {
                    panel.ShowScreenshots,
                    panel.HasScreenshotCommand,
                    panel.ScreenshotCommandText,
                    panel.HasScreenshotProblem,
                    CanRun = panel.ScreenshotsCommand.CanExecute(null),
                    panel.HasResult,
                },
                CancellationToken.None);

            Assert.True(before.ShowScreenshots);
            Assert.True(before.HasScreenshotCommand);
            Assert.Equal(Loc.Instance.Format(GitStrings.ScreenshotCommand, ScreenshotTestRig.Command), before.ScreenshotCommandText);
            Assert.False(before.HasScreenshotProblem);
            Assert.True(before.CanRun);
            Assert.False(before.HasResult);

            await RunAndWaitAsync(session, panel);

            var after = await session.Dispatch(
                () => new
                {
                    panel.LastResultText,
                    panel.LastResultIsError,
                    panel.HasHint,
                    panel.IsBusy,
                },
                CancellationToken.None);

            Assert.False(after.IsBusy);
            Assert.False(after.LastResultIsError, after.LastResultText);
            Assert.Contains("Pushed 1 screen(s)", after.LastResultText, StringComparison.Ordinal);
            Assert.Contains("home (before and after)", after.LastResultText, StringComparison.Ordinal);

            var body = platform.Current!.Body;
            Assert.Contains("## Screenshots", body, StringComparison.Ordinal);
            Assert.Contains("home-before.png?raw=true", body, StringComparison.Ordinal);
            Assert.Contains("home-after.png?raw=true", body, StringComparison.Ordinal);
            Assert.Single(platform.Edits);

            // The assets branch is on origin and the pull request's own files never contain an image.
            Assert.NotEmpty(GitTestShell.RunChecked(sandbox.Origin, "rev-parse", "--verify", "refs/heads/assets/main").Trim());
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Without_a_declaration_the_button_is_disabled_and_the_panel_says_why()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");

        var platform = new ScreenshotFakePlatform { Current = ScreenshotTestRig.PullRequest() };
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await AttachAndWaitForPullRequestAsync(session, panel, work);

            var state = await session.Dispatch(
                () => new
                {
                    panel.ShowScreenshots,
                    panel.HasScreenshotCommand,
                    panel.HasScreenshotProblem,
                    panel.ScreenshotCommandText,
                    CanRun = panel.ScreenshotsCommand.CanExecute(null),
                },
                CancellationToken.None);

            Assert.True(state.ShowScreenshots);
            Assert.False(state.HasScreenshotCommand);
            Assert.False(state.HasScreenshotProblem);
            Assert.Equal(Loc.Instance[GitStrings.ScreenshotNoCommand], state.ScreenshotCommandText);
            Assert.False(state.CanRun);
            Assert.Empty(platform.Edits);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task An_unusable_declaration_is_shown_instead_of_only_disabling_the_button()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        sandbox.CommitFile(work, ".forge/screenshots.json", "{\"command\": \"\", \"output\": \"shots\"}", "a broken declaration");

        var platform = new ScreenshotFakePlatform { Current = ScreenshotTestRig.PullRequest() };
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await AttachAndWaitForPullRequestAsync(session, panel, work);

            var state = await session.Dispatch(
                () => new
                {
                    panel.HasScreenshotCommand,
                    panel.HasScreenshotProblem,
                    panel.ScreenshotProblemText,
                    CanRun = panel.ScreenshotsCommand.CanExecute(null),
                },
                CancellationToken.None);

            Assert.False(state.HasScreenshotCommand);
            Assert.True(state.HasScreenshotProblem);
            Assert.Contains(".forge/screenshots.json", state.ScreenshotProblemText, StringComparison.Ordinal);
            Assert.False(state.CanRun);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task Without_a_pull_request_the_screenshot_group_stays_hidden()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        ScreenshotTestRig.DeclareScreenshots(sandbox, work);

        // The platform reports no pull request for this branch.
        var platform = new ScreenshotFakePlatform();
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await AttachAndWaitAsync(session, panel, work);

            var state = await session.Dispatch(
                () => new
                {
                    panel.ShowScreenshots,
                    panel.ScreenshotCommandText,
                    CanRun = panel.ScreenshotsCommand.CanExecute(null),
                },
                CancellationToken.None);

            Assert.False(state.ShowScreenshots);
            Assert.Equal(string.Empty, state.ScreenshotCommandText);
            Assert.False(state.CanRun);
        }
        finally
        {
            panel.Dispose();
        }
    }

    [Fact]
    public async Task A_capture_that_fails_is_reported_with_its_exit_code_and_the_description_stays_alone()
    {
        using var sandbox = GitTestSandbox.Create();
        var work = sandbox.CloneTracked("work");
        // The before side fails: the command only works on this branch.
        sandbox.CommitFile(
            work,
            ".forge/screenshots.json",
            "{\"command\": \"" + (OperatingSystem.IsWindows() ? "exit /b 9" : "exit 9") + "\", \"output\": \"shots\"}",
            "a command that fails");
        ScreenshotTestRig.PointOriginAtGitHub(sandbox, work);

        var platform = new ScreenshotFakePlatform { Current = ScreenshotTestRig.PullRequest() };
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var panel = NewPanel(platform);
        try
        {
            await AttachAndWaitForPullRequestAsync(session, panel, work);
            await RunAndWaitAsync(session, panel);

            var state = await session.Dispatch(
                () => new
                {
                    panel.LastResultText,
                    panel.LastResultIsError,
                    panel.HintText,
                    panel.HasHint,
                },
                CancellationToken.None);

            Assert.True(state.LastResultIsError);
            Assert.Contains("exit code 9", state.LastResultText, StringComparison.Ordinal);
            Assert.True(state.HasHint);
            Assert.Empty(platform.Edits);
            Assert.DoesNotContain("## Screenshots", platform.Current!.Body, StringComparison.Ordinal);
        }
        finally
        {
            panel.Dispose();
        }
    }

    // ------------------------------------------------------------ rig

    private static GitPanelViewModel NewPanel(ScreenshotFakePlatform platform)
    {
        var services = new GitPrServices(
            platform,
            new PrChecksPoller(platform, TimeProvider.System),
            static () => new PrChecksPollerOptions(),
            Notifier: null,
            Post: action => action())
        {
            ScreenshotOptions = new ScreenshotCaptureOptions { Timeout = TimeSpan.FromSeconds(60) },
        };
        return new GitPanelViewModel(
            new GitCli(),
            new GitPanelSettings(static () => GitPullStrategy.FastForwardOnly, static () => TimeSpan.FromSeconds(30)),
            static (_, _) => Task.FromResult(false),
            services);
    }

    private static Task AttachAndWaitForPullRequestAsync(HeadlessUnitTestSession session, GitPanelViewModel panel, string directory) =>
        session.Dispatch(
            async () =>
            {
                panel.Attach(new PanelContext("s1", directory, null));
                await WaitIdleAsync(panel);
                await GitPanelTesting.WaitUntilAsync(() => panel.HasPullRequest, "the pull request read");
                return 0;
            },
            CancellationToken.None);

    private static Task AttachAndWaitAsync(HeadlessUnitTestSession session, GitPanelViewModel panel, string directory) =>
        session.Dispatch(
            async () =>
            {
                panel.Attach(new PanelContext("s1", directory, null));
                await WaitIdleAsync(panel);
                await GitPanelTesting.WaitUntilAsync(
                    () => panel.PullRequestNoticeText.Length > 0 || panel.HasPullRequest,
                    "the pull request read");
                return 0;
            },
            CancellationToken.None);

    private static Task RunAndWaitAsync(HeadlessUnitTestSession session, GitPanelViewModel panel) =>
        session.Dispatch(
            async () =>
            {
                Assert.True(panel.ScreenshotsCommand.CanExecute(null), "the screenshot button is disabled");
                panel.ScreenshotsCommand.Execute(null);
                await WaitIdleAsync(panel);
                return 0;
            },
            CancellationToken.None);

    private static Task WaitIdleAsync(GitPanelViewModel panel) => GitPanelTesting.WaitIdleAsync(panel);
}
