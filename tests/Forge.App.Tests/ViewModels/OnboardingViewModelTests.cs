using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.App.Tests.Localization;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// The first-run wizard's model (wave W9): the three steps in order with a working back, "next" that becomes "done" and
/// finishes exactly once, the language choice written straight into the live preference, the provider step reading the real
/// catalogue, and the notification step following <see cref="INotificationPermissionGuide"/> — its state sentence, its
/// refusal turning into the step's hint, and a missing guide leaving nothing to open.
/// <para>Everything runs on the shared headless UI thread: the wizard re-raises the provider lines through the dispatcher and
/// its own refresh continuations return to the context it was built on.</para>
/// </summary>
[Collection("forge-app-ui")]
public sealed class OnboardingViewModelTests
{
    [Fact]
    public async Task The_wizard_walks_language_provider_notifications_and_back_again()
    {
        await SharedUi.RunAsync(() =>
        {
            using var _ = new LanguageScope(UiLanguage.Chinese);
            using var rig = new Rig();
            var model = rig.Model;

            Assert.True(model.IsLanguageStep);
            Assert.False(model.IsProviderStep);
            Assert.False(model.IsNotificationsStep);
            Assert.Equal(1, model.StepNumber);
            Assert.Equal(3, model.StepCount);
            Assert.False(model.CanGoBack);
            Assert.False(model.BackCommand.CanExecute(null));
            Assert.False(model.IsLastStep);
            Assert.Equal("下一步", model.NextText);

            // The first step has nowhere to go back to.
            model.BackCommand.Execute(null);
            Assert.True(model.IsLanguageStep);
            Assert.Equal(1, model.StepNumber);

            model.NextCommand.Execute(null);
            Assert.True(model.IsProviderStep);
            Assert.Equal(2, model.StepNumber);
            Assert.True(model.CanGoBack);
            Assert.True(model.BackCommand.CanExecute(null));
            Assert.Equal("下一步", model.NextText);

            model.NextCommand.Execute(null);
            Assert.True(model.IsNotificationsStep);
            Assert.Equal(3, model.StepNumber);
            Assert.True(model.IsLastStep);
            Assert.Equal("完成", model.NextText);

            model.BackCommand.Execute(null);
            Assert.True(model.IsProviderStep);
            Assert.Equal(2, model.StepNumber);
            Assert.False(model.IsLastStep);

            model.BackCommand.Execute(null);
            Assert.True(model.IsLanguageStep);
            Assert.False(model.CanGoBack);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Finishing_or_skipping_raises_finished_exactly_once()
    {
        await SharedUi.RunAsync(() =>
        {
            using var rig = new Rig();
            var model = rig.Model;
            var finished = 0;
            model.Finished += (_, _) => finished++;

            // Walking the steps is not finishing, however often back and next are pressed.
            model.NextCommand.Execute(null);
            model.BackCommand.Execute(null);
            model.NextCommand.Execute(null);
            Assert.Equal(0, finished);

            // Still only the last step finishes: this click enters it.
            model.NextCommand.Execute(null);
            Assert.True(model.IsLastStep);
            Assert.Equal(0, finished);

            model.NextCommand.Execute(null);
            Assert.Equal(1, finished);

            // A second click on "done" is not a second close.
            model.NextCommand.Execute(null);
            Assert.Equal(1, finished);

            // Skipping is the same decision, and it is also not taken twice.
            model.SkipCommand.Execute(null);
            model.SkipCommand.Execute(null);
            Assert.Equal(1, finished);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Skipping_before_walking_the_steps_finishes_once_without_touching_the_step()
    {
        await SharedUi.RunAsync(() =>
        {
            using var rig = new Rig();
            var model = rig.Model;
            var finished = 0;
            model.Finished += (_, _) => finished++;

            model.SkipCommand.Execute(null);

            Assert.Equal(1, finished);
            Assert.True(model.IsLanguageStep);
            Assert.Equal(1, model.StepNumber);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Choosing_a_language_writes_the_live_preference()
    {
        await SharedUi.RunAsync(() =>
        {
            using var rig = new Rig();
            var model = rig.Model;
            Assert.Equal(3, model.Languages.Count);
            Assert.Equal(
                [UiLanguage.System, UiLanguage.Chinese, UiLanguage.English],
                model.Languages.Select(static option => option.Language));

            var chinese = model.Languages[1];
            model.SelectedLanguage = chinese;

            Assert.Equal(UiLanguage.Chinese, rig.Preferences.Language);
            Assert.Equal(UiLanguage.Chinese, model.SelectedLanguage.Language);
            Assert.True(model.IsChineseLanguage);
            Assert.False(model.IsSystemLanguage);
            Assert.False(model.IsEnglishLanguage);

            // The radio buttons write the same preference; checking one unchecks the other without unchecking the choice.
            model.IsEnglishLanguage = true;
            Assert.Equal(UiLanguage.English, rig.Preferences.Language);
            Assert.True(model.IsEnglishLanguage);

            // Unchecking a button is what the group does to the one it left, so it must not write anything.
            model.IsChineseLanguage = false;
            Assert.Equal(UiLanguage.English, rig.Preferences.Language);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_provider_step_reads_the_real_catalogue_and_asks_the_shell_to_open_it()
    {
        await SharedUi.RunAsync(() =>
        {
            using var rig = new Rig();
            var model = rig.Model;
            var opened = 0;
            model.ProviderSetupRequested += (_, _) => opened++;
            model.NextCommand.Execute(null);

            // No provider is configured in this temporary data directory, so the status line says so and the button is what
            // the user gets instead.
            Assert.False(rig.Providers.Catalog.IsConfigured);
            Assert.False(model.IsProviderConnected);
            Assert.False(string.IsNullOrWhiteSpace(model.ProviderStatus));
            Assert.Equal(rig.Providers.Catalog.StatusText.Resolve(), model.ProviderStatus);

            model.ProviderSetupCommand.Execute(null);
            Assert.Equal(1, opened);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData((int)NotificationPermissionState.Granted, true)]
    [InlineData((int)NotificationPermissionState.Denied, true)]
    [InlineData((int)NotificationPermissionState.Unknown, true)]
    [InlineData((int)NotificationPermissionState.NotApplicable, false)]
    public async Task The_notification_step_shows_the_state_the_guide_reports(int stateValue, bool canOpen)
    {
        var state = (NotificationPermissionState)stateValue;
        await SharedUi.RunAsync(async () =>
        {
            using var rig = new Rig();
            rig.Guide.State = state;
            var model = rig.Model;

            await model.RefreshAsync(CancellationToken.None);

            Assert.Equal(NotificationPermissionTexts.StateText(state), model.NotificationState);
            Assert.Equal(canOpen, model.CanOpenNotificationSettings);
            Assert.False(model.HasNotificationHint);
        });
    }

    [Fact]
    public void The_permission_states_read_as_four_different_sentences()
    {
        // "Different" is the part a binding cannot check: the step must not show the same sentence whatever the OS said.
        var texts = Enum.GetValues<NotificationPermissionState>()
            .Select(NotificationPermissionTexts.StateText)
            .ToList();

        Assert.Equal(4, texts.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(texts, static text => string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public async Task Without_a_guide_the_step_explains_and_offers_nothing_to_open()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var rig = new Rig(withGuide: false);
            var model = rig.Model;

            await model.RefreshAsync(CancellationToken.None);

            Assert.Equal(OnboardingStrings.NotificationsNoGuide, model.NotificationState);
            Assert.False(model.CanOpenNotificationSettings);

            // Pressing the button is a no-op rather than a crash: there is no guide to ask and nothing to open.
            model.OpenNotificationSettingsCommand.Execute(null);
            SharedUi.Settle();
            Assert.False(model.HasNotificationHint);
        });
    }

    [Fact]
    public async Task A_platform_that_refuses_to_open_its_settings_leaves_a_hint()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var rig = new Rig();
            rig.Guide.State = NotificationPermissionState.Denied;
            rig.Guide.Opens = false;
            var model = rig.Model;
            model.NextCommand.Execute(null);
            model.NextCommand.Execute(null);
            await model.RefreshAsync(CancellationToken.None);
            Assert.True(model.CanOpenNotificationSettings);

            model.OpenNotificationSettingsCommand.Execute(null);
            SharedUi.Settle();

            Assert.True(rig.Guide.OpenCalls > 0);
            Assert.True(model.HasNotificationHint);
            Assert.Equal(OnboardingStrings.NotificationsHint, model.NotificationHint);
        });
    }

    [Fact]
    public async Task Opening_the_platform_settings_twice_leaves_no_hint_when_it_worked()
    {
        await SharedUi.RunAsync(async () =>
        {
            using var rig = new Rig();
            rig.Guide.State = NotificationPermissionState.Granted;
            rig.Guide.Opens = true;
            var model = rig.Model;
            await model.RefreshAsync(CancellationToken.None);

            model.OpenNotificationSettingsCommand.Execute(null);
            SharedUi.Settle();

            Assert.True(rig.Guide.OpenCalls > 0);
            Assert.False(model.HasNotificationHint);
            Assert.Equal(string.Empty, model.NotificationHint);
        });
    }

    [Fact]
    public async Task A_disposed_wizard_stops_listening_to_the_preferences_and_the_catalogue()
    {
        await SharedUi.RunAsync(() =>
        {
            using var rig = new Rig();
            var model = rig.Model;
            var raised = new List<string?>();
            model.PropertyChanged += (_, args) => raised.Add(args.PropertyName);

            model.Dispose();

            // Both subscriptions are gone: writing the preference and reloading the catalogue must neither throw nor notify.
            rig.Preferences.Language = UiLanguage.English;
            rig.Preferences.ProjectsCloneDirectory = rig.Directory.Path;
            rig.Providers.Reload();
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(raised);
            return Task.CompletedTask;
        });
    }

    // ---------------------------------------------------------------- the rig

    /// <summary>The three live services the wizard is built over, plus a throw-away data directory for the catalogue.</summary>
    private sealed class Rig : IDisposable
    {
        public Rig(bool withGuide = true)
        {
            Preferences = new AppPreferences();
            Providers = new ProviderServices(
                Path.Combine(Directory.Path, "settings.json"),
                Directory.Path,
                new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
            Guide = new FakeGuide();
            Model = new OnboardingViewModel(Preferences, Providers, withGuide ? Guide : null);
        }

        public TempDirectory Directory { get; } = new();

        public AppPreferences Preferences { get; }

        public ProviderServices Providers { get; }

        public FakeGuide Guide { get; }

        public OnboardingViewModel Model { get; }

        public void Dispose()
        {
            Model.Dispose();
            Directory.Dispose();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = TestTempFiles.NewDirectory("forge-onboarding-");
        }

        public string Path { get; }

        public void Dispose() => TestTempFiles.DeleteTree(Path);
    }

    /// <summary>A permission guide that answers with what the test set, and counts the attempts to open the OS settings.</summary>
    private sealed class FakeGuide : INotificationPermissionGuide
    {
        public NotificationPermissionState State { get; set; } = NotificationPermissionState.Unknown;

        public bool Opens { get; set; }

        public int OpenCalls { get; private set; }

        public Task<NotificationPermissionState> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(State);

        public Task<bool> OpenSettingsAsync(CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            return Task.FromResult(Opens);
        }
    }
}