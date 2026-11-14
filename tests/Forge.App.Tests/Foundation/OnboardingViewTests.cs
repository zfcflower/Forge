using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.App.Preferences;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Foundation;

/// <summary>
/// The first-run wizard's real view, run headless (wave W9). The XAML has to load, and its compiled bindings have to reach
/// <see cref="OnboardingViewModel"/>, because the shell mounts the wizard through a template registered for the view model's
/// type: a view that binds to nothing would render an empty card and no view-model test would notice. The navigation buttons
/// are found by their automation ids and compared with the model's own commands and texts, so the bindings are proven live.
/// </summary>
[Collection("forge-app-ui")]
public sealed class OnboardingViewTests
{
    [Fact]
    public async Task The_wizard_renders_its_step_and_the_navigation_buttons_the_shell_relies_on()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var rendered = await SharedUi.Session.Dispatch(
            () =>
            {
                var root = TestTempFiles.NewDirectory("forge-onboarding-view-");
                try
                {
                    using var model = new OnboardingViewModel(new AppPreferences(), Services(root), new GrantedGuide());
                    var window = SharedUi.Show(new OnboardingView { DataContext = model }, 900, 780);
                    SharedUi.Settle(window);

                    var next = Button(window, "onboarding-next")!;
                    var back = Button(window, "onboarding-back")!;
                    var skip = Button(window, "onboarding-skip")!;

                    var snapshot = new Rendered(
                        Ids: window.GetVisualDescendants().Select(AutomationProperties.GetAutomationId).OfType<string>().ToList(),
                        Next: Label(next),
                        Back: Label(back),
                        Skip: Label(skip),
                        NextBound: ReferenceEquals(next.Command, model.NextCommand),
                        BackBound: ReferenceEquals(back.Command, model.BackCommand),
                        SkipBound: ReferenceEquals(skip.Command, model.SkipCommand),
                        BackEnabled: back.IsEffectivelyEnabled,
                        Title: TextOf(TextBlock(window, "onboarding-step-title")),
                        Caption: TextOf(TextBlock(window, "onboarding-step-caption")),
                        ExpectedTitle: model.StepTitle,
                        ExpectedCaption: model.StepCaption,
                        ExpectedNext: model.NextText,
                        ExpectedBack: OnboardingStrings.Back,
                        ExpectedSkip: OnboardingStrings.Skip);

                    window.Close();
                    return snapshot;
                }
                finally
                {
                    TestTempFiles.DeleteTree(root);
                }
            },
            budget.Token);

        // The three buttons the shell's flow is driven by exist, and each carries the model's own text and command.
        Assert.Contains("onboarding-next", rendered.Ids);
        Assert.Contains("onboarding-back", rendered.Ids);
        Assert.Contains("onboarding-skip", rendered.Ids);
        Assert.Equal(rendered.ExpectedNext, rendered.Next);
        Assert.Equal(rendered.ExpectedBack, rendered.Back);
        Assert.Equal(rendered.ExpectedSkip, rendered.Skip);
        Assert.True(rendered.NextBound, "the next button is not bound to the model's command");
        Assert.True(rendered.BackBound, "the back button is not bound to the model's command");
        Assert.True(rendered.SkipBound, "the skip button is not bound to the model's command");

        // The first step's back button is the disabled one: it follows the command's CanExecute (Avalonia turns a command
        // that cannot execute into an effectively disabled button, while the local IsEnabled stays as it was set).
        Assert.False(rendered.BackEnabled, "the first step has nowhere to go back to");

        // The step frame renders the model's texts rather than a static placeholder.
        Assert.Equal(rendered.ExpectedTitle, rendered.Title);
        Assert.Equal(rendered.ExpectedCaption, rendered.Caption);
        Assert.False(string.IsNullOrWhiteSpace(rendered.Title));
    }

    [Fact]
    public async Task Each_step_draws_only_its_own_body_and_the_line_the_model_gives_it()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var rendered = await SharedUi.Session.Dispatch(
            () =>
            {
                var root = TestTempFiles.NewDirectory("forge-onboarding-view-steps-");
                try
                {
                    using var model = new OnboardingViewModel(new AppPreferences(), Services(root), new GrantedGuide());
                    var window = SharedUi.Show(new OnboardingView { DataContext = model }, 900, 780);
                    SharedUi.Settle(window);

                    // The language step: its three choices are drawn, and the other two steps' bodies are not.
                    var languageVisible = Visible(window, "onboarding-language-system");
                    var providerHiddenOnFirstStep = !Visible(window, "onboarding-provider-status");
                    var notificationHiddenOnFirstStep = !Visible(window, "onboarding-notification-status");

                    model.NextCommand.Execute(null);
                    SharedUi.Settle(window);
                    var providerVisible = Visible(window, "onboarding-provider-status");
                    var providerText = TextOf(TextBlock(window, "onboarding-provider-status"));
                    var expectedProviderText = model.ProviderStatus;
                    var languageHiddenOnProviderStep = !Visible(window, "onboarding-language-system");

                    model.NextCommand.Execute(null);
                    SharedUi.Settle(window);
                    var notificationVisible = Visible(window, "onboarding-notification-status");
                    var notificationText = TextOf(TextBlock(window, "onboarding-notification-status"));
                    var expectedNotificationText = model.NotificationState;
                    var providerHiddenOnNotificationStep = !Visible(window, "onboarding-provider-status");

                    var snapshot = new Steps(
                        languageVisible,
                        providerHiddenOnFirstStep,
                        notificationHiddenOnFirstStep,
                        providerVisible,
                        providerText,
                        expectedProviderText,
                        languageHiddenOnProviderStep,
                        notificationVisible,
                        notificationText,
                        expectedNotificationText,
                        providerHiddenOnNotificationStep);

                    window.Close();
                    return snapshot;
                }
                finally
                {
                    TestTempFiles.DeleteTree(root);
                }
            },
            budget.Token);

        Assert.True(rendered.LanguageVisible, "the first step draws no language choice");
        Assert.True(rendered.ProviderHiddenOnFirstStep, "the provider body is drawn before its step");
        Assert.True(rendered.NotificationHiddenOnFirstStep, "the notification body is drawn before its step");

        Assert.True(rendered.ProviderVisible, "the provider step draws no status line");
        Assert.Equal(rendered.ExpectedProviderText, rendered.ProviderText);

        Assert.True(rendered.NotificationVisible, "the notification step draws no status line");
        Assert.Equal(rendered.ExpectedNotificationText, rendered.NotificationText);

        // One body at a time: the previous step's body goes away with the step.
        Assert.True(rendered.LanguageHiddenOnProviderStep);
        Assert.True(rendered.ProviderHiddenOnNotificationStep);
    }

    // ---------------------------------------------------------------- helpers

    private static ProviderServices Services(string root) =>
        new(Path.Combine(root, "settings.json"), root, new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private static Visual? Find(Window window, string automationId) =>
        window.GetVisualDescendants()
            .FirstOrDefault(control => string.Equals(AutomationProperties.GetAutomationId(control), automationId, StringComparison.Ordinal));

    private static Button? Button(Window window, string automationId) => Find(window, automationId) as Button;

    private static TextBlock? TextBlock(Window window, string automationId) => Find(window, automationId) as TextBlock;

    private static bool Visible(Window window, string automationId) => Find(window, automationId) is { IsEffectivelyVisible: true };

    private static string? Label(Button button) => button.Content as string ?? button.Content?.ToString();

    private static string? TextOf(TextBlock? text) => text?.Text;

    private sealed record Rendered(
        IReadOnlyList<string> Ids,
        string? Next,
        string? Back,
        string? Skip,
        bool NextBound,
        bool BackBound,
        bool SkipBound,
        bool BackEnabled,
        string? Title,
        string? Caption,
        string ExpectedTitle,
        string ExpectedCaption,
        string ExpectedNext,
        string ExpectedBack,
        string ExpectedSkip);

    private sealed record Steps(
        bool LanguageVisible,
        bool ProviderHiddenOnFirstStep,
        bool NotificationHiddenOnFirstStep,
        bool ProviderVisible,
        string? ProviderText,
        string ExpectedProviderText,
        bool LanguageHiddenOnProviderStep,
        bool NotificationVisible,
        string? NotificationText,
        string ExpectedNotificationText,
        bool ProviderHiddenOnNotificationStep);

    /// <summary>A guide that grants the permission without sending anything anywhere.</summary>
    private sealed class GrantedGuide : INotificationPermissionGuide
    {
        public Task<NotificationPermissionState> GetStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(NotificationPermissionState.Granted);

        public Task<bool> OpenSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}