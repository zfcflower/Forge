using Avalonia.Headless;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels.Settings;
using Forge.Mcp;
using Xunit;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// The MCP settings page's state machine, headless: a 401 turns a row into "sign-in required" in the page's own language, a
/// finished sign-in flips the row's action, a resource subscription flips its badge and reports a rejected one, an update
/// notification re-lists the resources once per burst, and a notification never cancels an action the user started. Every
/// case runs against an injected <see cref="McpServices"/> — the fake HTTP handler answers the server and its authorization
/// server, the loopback transport answers the rest — so no process, socket or real browser is involved.
/// </summary>
[Collection("forge-app-ui")]
public sealed class McpSettingsStateTests : IDisposable
{
    private static readonly TimeSpan StepBudget = TimeSpan.FromSeconds(120);

    /// <summary>One server over streamable HTTP; the fake handler answers both it and its authorization server.</summary>
    private const string HttpServerConfig = """{"mcpServers":{"srv":{"url":"http://127.0.0.1:9/mcp"}}}""";

    private readonly string root = TestTempFiles.NewDirectory("forge-mcp-state-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    // ------------------------------------------------------------------ OAuth

    [Fact]
    public async Task A_401_row_offers_the_sign_in_action_in_the_current_language()
    {
        var seen = await SharedUi.Session.Dispatch(
            async () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                await using var page = McpPage.OpenHttp(root, new FakeMcpOAuthHttp(), new LoopbackOAuthBrowser());
                var row = await page.RowAsync("srv");

                await page.StartAsync("srv");
                await McpPage.WaitUntilAsync(() => row.NeedsSignIn, "the row to need a sign-in");

                // The view binds the sign-in button to NeedsSignIn and the sign-out one to IsSignedIn.
                return (row.NeedsSignIn, row.IsSignedIn, row.HasSignInAction, row.CanSignIn, row.HasError, row.ErrorText);
            },
            StepTimeout());

        Assert.True(seen.NeedsSignIn);
        Assert.False(seen.IsSignedIn);
        Assert.True(seen.HasSignInAction);
        Assert.True(seen.CanSignIn);
        Assert.True(seen.HasError);
        // The sentence is the page's and in the page's language; the manager's constant belongs to the state machine.
        Assert.Equal(McpSettingsStrings.NeedsSignIn, seen.ErrorText);
        Assert.NotEqual(McpServerManager.SignInRequiredMessage, seen.ErrorText);
    }

    [Fact]
    public async Task A_finished_sign_in_switches_the_row_from_the_sign_in_action_to_the_sign_out_one()
    {
        var seen = await SharedUi.Session.Dispatch(
            async () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var http = new FakeMcpOAuthHttp();
                var browser = new LoopbackOAuthBrowser();
                await using var page = McpPage.OpenHttp(root, http, browser);
                var row = await page.RowAsync("srv");
                await page.StartAsync("srv");
                await McpPage.WaitUntilAsync(() => row.NeedsSignIn, "the sign-in action");

                row.SignInCommand.Execute(null);
                await McpPage.WaitUntilAsync(() => row.IsSignedIn, "the sign-in to finish");

                return (
                    row.IsSignedIn,
                    row.NeedsSignIn,
                    SignedIn: page.Services.IsSignedIn("srv"),
                    Ready: page.Services.Manager.GetState("srv").IsReady,
                    Opened: browser.OpenedCount,
                    Tokens: http.TokensIssued,
                    Bearer: http.McpAuthorizations.LastOrDefault(),
                    Stored: File.Exists(Path.Combine(root, McpTokenStore.SecretsFileName)));
            },
            StepTimeout());

        Assert.True(seen.IsSignedIn);
        Assert.False(seen.NeedsSignIn);
        Assert.True(seen.SignedIn);
        Assert.True(seen.Ready);
        Assert.Equal(1, seen.Opened);
        Assert.Equal(1, seen.Tokens);
        // The reconnected transport carries the token the sign-in minted, and the page kept it out of mcp.json.
        Assert.Equal("Bearer " + FakeMcpOAuthHttp.AccessToken, seen.Bearer);
        Assert.True(seen.Stored);
    }

    // ------------------------------------------------------------------ resources

    [Fact]
    public async Task Subscribing_flips_the_badge_and_a_rejected_subscribe_is_reported_in_the_status_bar()
    {
        var seen = await SharedUi.Session.Dispatch(
            async () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                await using var page = McpPage.OpenLoopback(root, ResourceServer(), out _);
                var row = await page.RowAsync("srv");
                row.IsExpanded = true;
                await McpPage.WaitUntilAsync(() => row.Resources.Count == 2, "the resource list");

                var first = row.Resources[0];
                var before = (first.IsSubscribed, first.SubscribeText, first.IsSubscribedBadge);
                first.SubscribeCommand.Execute(null);
                await McpPage.WaitUntilAsync(() => first.IsSubscribed, "the subscription to land");
                var after = (first.IsSubscribed, first.SubscribeText, first.IsSubscribedBadge);
                var held = page.Services.IsSubscribed("srv", first.Uri);

                row.Resources[1].SubscribeCommand.Execute(null);
                await McpPage.WaitUntilAsync(() => page.Section.HasStatus, "the failure");

                return (before, after, held, page.Section.StatusText, page.Section.StatusIsError);
            },
            StepTimeout());

        Assert.False(seen.before.IsSubscribed);
        Assert.Equal(McpSettingsStrings.ResourceSubscribe, seen.before.SubscribeText);
        Assert.False(seen.before.IsSubscribedBadge);
        Assert.True(seen.after.IsSubscribed);
        Assert.Equal(McpSettingsStrings.ResourceUnsubscribe, seen.after.SubscribeText);
        Assert.True(seen.after.IsSubscribedBadge);
        // The manager holds the subscription, not just the row: that set is what a reconnect replays.
        Assert.True(seen.held);
        Assert.True(seen.StatusIsError);
        Assert.Contains(ResourceRefused, seen.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_burst_of_resource_updates_re_lists_the_resources_once()
    {
        var seen = await SharedUi.Session.Dispatch(
            async () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                await using var page = McpPage.OpenLoopback(root, ResourceServer(), out var transports);
                var row = await page.RowAsync("srv");
                row.IsExpanded = true;
                await McpPage.WaitUntilAsync(() => row.Resources.Count == 2, "the resource list");
                var transport = Assert.Single(transports);
                var before = transport.CountSent(McpProtocol.ResourcesList);

                transport.Push(Notification(McpProtocol.ResourcesUpdated, """{"uri":"res://one"}"""));
                transport.Push(Notification(McpProtocol.ResourcesUpdated, """{"uri":"res://two"}"""));
                await McpPage.WaitUntilAsync(
                    () => transport.CountSent(McpProtocol.ResourcesList) > before,
                    "the update to re-list the resources");
                // Long enough for a second list if each notification had started one.
                await Task.Delay(250).ConfigureAwait(true);

                return (Lists: transport.CountSent(McpProtocol.ResourcesList) - before, row.HasResourceUpdated, row.ResourceUpdatedText);
            },
            StepTimeout());

        Assert.Equal(1, seen.Lists);
        Assert.True(seen.HasResourceUpdated);
        Assert.Contains("res://two", seen.ResourceUpdatedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_notification_refresh_survives_the_action_token_a_click_takes()
    {
        var seen = await SharedUi.Session.Dispatch(
            async () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                var listCalls = 0;
                var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var script = ResourceServer().On(McpProtocol.ResourcesList, _ =>
                {
                    // The second list is the one the update notification started: hold it open so the click below lands while
                    // it is still in flight, which is exactly the window the defect lived in.
                    if (Interlocked.Increment(ref listCalls) < 2)
                    {
                        return FakeMcpOAuthHttp.ResourcesJson;
                    }

                    parked.TrySetResult();
                    release.Task.Wait(TimeSpan.FromSeconds(30));
                    return ThreeResourcesJson;
                });
                await using var page = McpPage.OpenLoopback(root, script, out var transports);
                var row = await page.RowAsync("srv");
                row.IsExpanded = true;
                await McpPage.WaitUntilAsync(() => row.Resources.Count == 2, "the resource list");
                var transport = Assert.Single(transports);

                transport.Push(Notification(McpProtocol.ResourcesUpdated, """{"uri":"res://one"}"""));
                await parked.Task.ConfigureAwait(true);

                // A notification-driven refresh is not an action: it must leave the page's buttons usable while it runs, or a
                // burst of server notifications locks the user out of their own settings page.
                Assert.True(row.TestCommand.CanExecute(null), "a notification refresh must not disable the user's buttons");

                // A click takes the section's action token. It must cancel only what that token covers, never the refresh a
                // notification started — that one is cancelled by the panel going away, not by the user using the page.
                row.TestCommand.Execute(null);
                await Task.Delay(150).ConfigureAwait(true);
                release.TrySetResult();
                await McpPage.WaitUntilAsync(() => row.Resources.Count == 3, "the notification's re-list to apply");

                return (Count: row.Resources.Count, Lists: transport.CountSent(McpProtocol.ResourcesList));
            },
            StepTimeout());

        Assert.Equal(3, seen.Count);
        Assert.Equal(2, seen.Lists);
    }

    [Fact]
    public async Task A_resource_update_during_a_user_action_lists_nothing_and_lets_the_action_finish()
    {
        var seen = await SharedUi.Session.Dispatch(
            async () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                await using var page = McpPage.OpenLoopback(root, ResourceServer(), out var transports);
                var row = await page.RowAsync("srv");
                row.IsExpanded = true;
                await McpPage.WaitUntilAsync(() => row.Resources.Count == 2, "the resource list");
                var transport = Assert.Single(transports);
                var before = transport.CountSent(McpProtocol.ResourcesList);

                // Park the restart inside the transport's stop, so the notification below lands while the action is running.
                var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                transport.StopHook = async () =>
                {
                    transport.StopHook = null;
                    stopping.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                };

                try
                {
                    row.RestartCommand.Execute(null);
                    await stopping.Task.ConfigureAwait(true);
                    transport.Push(Notification(McpProtocol.ResourcesUpdated, """{"uri":"res://one"}"""));
                    await McpPage.WaitUntilAsync(() => row.HasResourceUpdated, "the update line");
                    var duringAction = transport.CountSent(McpProtocol.ResourcesList) - before;

                    release.TrySetResult();
                    await McpPage.WaitUntilAsync(
                        () => page.Services.Manager.GetState("srv").IsReady,
                        "the restart to finish");
                    return (DuringAction: duringAction, Restarted: true, row.HasResourceUpdated);
                }
                finally
                {
                    transport.StopHook = null;
                    release.TrySetResult();
                }
            },
            StepTimeout());

        // A re-list while an action runs is dropped (the action owns the page), and the update line still says what changed.
        Assert.Equal(0, seen.DuringAction);
        Assert.True(seen.Restarted);
        Assert.True(seen.HasResourceUpdated);
    }

    // ------------------------------------------------------------------ fixtures

    private const string ResourceRefused = "resource is not subscribable";

    private const string ThreeResourcesJson =
        """{"resources":[{"uri":"res://one"},{"uri":"res://two"},{"uri":"res://three"}]}""";

    /// <summary>A server with two resources whose second subscription the server rejects.</summary>
    private static ScriptedMcpServer ResourceServer() => new ScriptedMcpServer()
        .On(McpProtocol.Initialize, _ => ScriptedMcpServer.InitializeWithResourcesJson)
        .On(McpProtocol.ToolsList, _ => """{"tools":[]}""")
        .On(McpProtocol.ResourcesList, _ => FakeMcpOAuthHttp.ResourcesJson)
        .On(McpProtocol.ResourcesSubscribe, _ => "{}")
        .On(McpProtocol.ResourcesUnsubscribe, _ => "{}")
        .OnError(
            McpProtocol.ResourcesSubscribe,
            request => request.GetProperty("params").GetProperty("uri").GetString() == "res://two"
                ? $$"""{"code":-32002,"message":"{{ResourceRefused}}"}"""
                : null);

    private static string Notification(string method, string parametersJson) =>
        $$"""{"jsonrpc":"2.0","method":"{{method}}","params":{{parametersJson}}}""";

    private static CancellationToken StepTimeout() => new CancellationTokenSource(StepBudget).Token;

    /// <summary>
    /// One settings page over an injected <see cref="McpServices"/>: a throw-away <c>mcp.json</c>, its preferences, the manager
    /// over the given HTTP handler or transport factory, and the section view model that draws them. Everything is built and
    /// used on the shared UI thread — the section's rows only report to the thread they were built on.
    /// </summary>
    private sealed class McpPage : IAsyncDisposable
    {
        private readonly PreferencesStore preferences;

        private McpPage(PreferencesStore preferences, McpServices services, McpSettingsSectionViewModel section)
        {
            this.preferences = preferences;
            Services = services;
            Section = section;
        }

        public McpServices Services { get; }

        public McpSettingsSectionViewModel Section { get; }

        public static McpPage OpenHttp(string root, FakeMcpOAuthHttp http, LoopbackOAuthBrowser browser)
        {
            TestTempFiles.WriteFile(root, "global/mcp.json", HttpServerConfig);
            var client = new HttpClient(http) { Timeout = Timeout.InfiniteTimeSpan };
            return Open(root, client, transportFactory: null, new McpTokenStore(root), browser);
        }

        public static McpPage OpenLoopback(string root, ScriptedMcpServer script, out List<LoopbackMcpTransport> transports)
        {
            TestTempFiles.WriteFile(root, "global/mcp.json", HttpServerConfig);
            var created = new List<LoopbackMcpTransport>();
            transports = created;
            return Open(
                root,
                http: null,
                config =>
                {
                    var transport = new LoopbackMcpTransport(config.Name) { Responder = script.Respond };
                    created.Add(transport);
                    return transport;
                },
                tokenStore: null,
                oauthUi: null);
        }

        /// <summary>The row the page drew for one server, once the (asynchronous) file read has produced it.</summary>
        public async Task<McpServerRowViewModel> RowAsync(string name)
        {
            Section.Reload();
            await WaitUntilAsync(() => Section.Servers.Any(row => row.Name == name), $"the row for '{name}'");
            return Section.Servers.First(row => row.Name == name);
        }

        /// <summary>Starts one server; a failure is the manager's state to report, which is what the page reads.</summary>
        public async Task StartAsync(string name)
        {
            try
            {
                await Services.Manager.StartAsync(name, CancellationToken.None).ConfigureAwait(true);
            }
            catch (McpException)
            {
            }
        }

        /// <summary>
        /// Waits for <paramref name="condition"/> while the dispatcher keeps running: inside an async dispatch the queued work
        /// (the section posts its rebuilds there) is processed between the yields, so a posted state change is applied here.
        /// </summary>
        public static async Task WaitUntilAsync(Func<bool> condition, string what)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail($"{what} did not happen within 30 s");
                }

                await Task.Delay(15).ConfigureAwait(true);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Section.Dispose();
            await Services.DisposeAsync().ConfigureAwait(true);
            await preferences.DisposeAsync().ConfigureAwait(true);
        }

        private static McpPage Open(
            string root,
            HttpClient? http,
            Func<McpServerConfig, IMcpTransport>? transportFactory,
            IMcpTokenStore? tokenStore,
            IMcpOAuthUi? oauthUi)
        {
            var preferences = new PreferencesStore(Path.Combine(root, "settings.json"));
            // A failed start schedules a reconnect: park it as far away as the preference allows, so the assertions see the
            // state the action produced rather than a retry landing in the middle of them.
            preferences.Current.McpReconnectInitialDelaySeconds = AppPreferences.MaxMcpReconnectInitialDelaySeconds;
            var services = McpServices.Create(
                new McpConfigStore(Path.Combine(root, "global", "mcp.json"), null),
                preferences,
                notifications: null,
                http,
                transportFactory,
                tokenStore,
                oauthUi);
            var section = new McpSettingsSectionViewModel(preferences.Current, services, default, root);
            return new McpPage(preferences, services, section);
        }
    }
}
