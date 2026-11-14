using System.Reflection;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// The line a running turn shows while its next request waits on the provider's local budget: on the turn card, never an
/// error, and — unlike a provider notice — never a toast, because a wait is the explanation of why the turn is quiet, not
/// something to interrupt the user with. The sink is driven directly (the worker-thread entry point the runtime calls),
/// the same way the routing tests are.
/// </summary>
[Collection("forge-app-ui")]
public sealed class RateLimitBudgetWaitTests
{
    private const string SessionId = "budget-wait-session";
    private const string ProviderId = "openai";

    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The worker-thread event sink of the session; every runtime event goes through it.</summary>
    private static readonly MethodInfo Sink = typeof(SessionWorkspaceViewModel).GetMethod(
        "OnAgentEvent",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("SessionWorkspaceViewModel.OnAgentEvent is gone; the budget-wait test cannot drive the sink.");

    [Fact]
    public async Task A_budget_wait_leaves_one_quiet_line_with_the_provider_and_the_seconds_and_no_toast()
    {
        using var language = new Forge.App.Tests.Localization.LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var outcome = await session.Dispatch(
            async () =>
            {
                var notifications = new RecordingNotifications();
                using var workspace = CreateWorkspace(notifications);

                // The unconfigured-probe workspace toasts "no provider is set" from its own constructor; that one is
                // not the toast under test, so the record starts empty once the workspace exists.
                notifications.Reset();
                await Task.Run(() => Emit(workspace, new AgentBudgetWait("root", ProviderId, TimeSpan.FromSeconds(7))));
                Assert.True(
                    await PumpUntilAsync(() => Notices(workspace).Count == 1),
                    "the wait line never reached the turn card");
                return (Lines: Notices(workspace), Toasts: notifications.Calls);
            },
            CancellationToken.None);

        // The line names the provider and the seconds it is waiting; it is not an error, and no toast was raised for it.
        var line = Assert.Single(outcome.Lines);
        Assert.Equal("本地限流：等待 openai 恢复额度，约 7 秒", line.Text);
        Assert.False(line.IsError);
        Assert.True(outcome.Toasts.Count == 0, "a wait is a status line, not an interruption: " + string.Join("; ", outcome.Toasts));
    }

    /// <summary>
    /// The first request of a turn can be the one that waits: <c>Card</c> begins the root's turn when no card exists yet,
    /// so the wait line is not lost to a turn that has not produced anything yet.
    /// </summary>
    [Fact]
    public async Task A_wait_on_a_turn_with_no_card_yet_still_has_a_place_to_show_itself()
    {
        using var language = new Forge.App.Tests.Localization.LanguageScope(UiLanguage.Chinese);
        var session = SharedUi.Session;
        var lines = await session.Dispatch(
            async () =>
            {
                using var workspace = CreateWorkspace();
                // No turn has begun: the transcript holds no card at all yet.
                Assert.Empty(workspace.Transcript.Items.OfType<TurnCardViewModel>());
                await Task.Run(() => Emit(workspace, new AgentBudgetWait("root", ProviderId, TimeSpan.FromSeconds(4))));
                Assert.True(
                    await PumpUntilAsync(() => Notices(workspace).Count == 1),
                    "the wait line never reached the card the wait itself began");
                return Notices(workspace);
            },
            CancellationToken.None);

        Assert.Contains("openai", Assert.Single(lines).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sentence_names_the_provider_and_rounds_the_seconds_up_in_both_languages()
    {
        using var chinese = new Forge.App.Tests.Localization.LanguageScope(UiLanguage.Chinese);
        Assert.Equal("本地限流：等待 openai 恢复额度，约 7 秒", RateLimitStrings.Waiting("openai", TimeSpan.FromSeconds(6.2)).Resolve());

        using var english = new Forge.App.Tests.Localization.LanguageScope(UiLanguage.English);
        Assert.Equal("Rate limit: waiting for openai to refill, about 7s", RateLimitStrings.Waiting("openai", TimeSpan.FromSeconds(6.2)).Resolve());
    }

    private static List<TurnNoticeViewModel> Notices(SessionWorkspaceViewModel workspace) =>
        workspace.Transcript.Items
            .OfType<TurnCardViewModel>()
            .SelectMany(static card => card.Blocks.OfType<TurnNoticeViewModel>())
            .ToList();

    private static void Emit(SessionWorkspaceViewModel workspace, AgentEvent value) => Sink.Invoke(workspace, [value]);

    private static SessionWorkspaceViewModel CreateWorkspace(INotifications? notifications = null)
    {
        var info = new SessionInfo(SessionId, Path.GetTempPath(), "demo session", At);
        var store = new EmptySessionStore(At);
        return new SessionWorkspaceViewModel(
            info,
            new SessionRuntime(
                new AgentRuntimeServices(
                    static _ => new MissingCredentialProvider("unconfigured", ProviderStrings.SectionMissingText()),
                    HostClient: null,
                    store,
                    new EmptySessionCatalog(At),
                    new EmptyTodoStore("t1", At),
                    new ModelSettings("model")),
                info),
            checkpoints: null,
            store,
            TestProviderServices.UnconfiguredProbe(),
            notifications: notifications);
    }

    private static Task<bool> PumpUntilAsync(Func<bool> condition) => TestPump.UntilAsync(condition);

    /// <summary>Counts every toast that was asked for; a wait must ask for none.</summary>
    private sealed class RecordingNotifications : INotifications
    {
        private readonly List<string> calls = [];

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (calls)
                {
                    return [.. calls];
                }
            }
        }

        public void Reset()
        {
            lock (calls)
            {
                calls.Clear();
            }
        }

        public Task NotifyAsync(
            NotificationLevel level,
            LocText title,
            LocText body,
            string? key = null,
            CancellationToken ct = default)
        {
            lock (calls)
            {
                calls.Add($"{level}: {body.Resolve()}");
            }

            return Task.CompletedTask;
        }
    }
}
