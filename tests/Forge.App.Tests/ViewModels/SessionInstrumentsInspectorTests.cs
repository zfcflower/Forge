using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the strip's "running resources" row (wave W8): the row is offered only when a window wired the overlay (a
/// standalone view must not show a dead entry), its click opens that window, and its label carries the session's live
/// count - re-raised on a change and silent when the count did not move, because the session pushes the count every tick.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionInstrumentsInspectorTests
{
    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Without_a_wired_overlay_the_row_is_not_offered()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                using var instruments = Create(openInspector: null);
                var before = instruments.OpenInspectorMenuText;
                instruments.OpenInspectorCommand.Execute(null);
                return (
                    instruments.CanOpenInspector,
                    CanExecute: instruments.OpenInspectorCommand.CanExecute(null),
                    Before: before,
                    After: instruments.OpenInspectorMenuText);
            },
            CancellationToken.None);

        Assert.False(state.CanOpenInspector);
        Assert.False(state.CanExecute);
        // Executing a command that cannot run is a no-op: nothing opens and nothing changes.
        Assert.Equal("运行中的资源…", state.Before);
        Assert.Equal(state.Before, state.After);
    }

    [Fact]
    public async Task The_row_opens_the_wired_overlay_once()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                var opened = 0;
                using var instruments = Create(openInspector: () => opened++);
                var canOpen = instruments.CanOpenInspector;
                var canExecute = instruments.OpenInspectorCommand.CanExecute(null);
                instruments.OpenInspectorCommand.Execute(null);
                return (opened, canOpen, canExecute);
            },
            CancellationToken.None);

        Assert.Equal(1, state.opened);
        Assert.True(state.canOpen);
        Assert.True(state.canExecute);
    }

    [Fact]
    public async Task The_menu_text_carries_the_live_count_and_nothing_else()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var language = new LanguageScope(UiLanguage.Chinese);
                using var instruments = Create(openInspector: () => { });
                var empty = instruments.OpenInspectorMenuText;

                instruments.NoteRunningResources(2);
                var two = (instruments.OpenInspectorMenuText, instruments.RunningResources);

                instruments.NoteRunningResources(0);
                var zero = (instruments.OpenInspectorMenuText, instruments.RunningResources);
                return (Empty: empty, Two: two, Zero: zero);
            },
            CancellationToken.None);

        Assert.Equal("运行中的资源…", state.Empty);
        Assert.Equal(("运行中的资源(2)…", 2), state.Two);
        Assert.Equal(("运行中的资源…", 0), state.Zero);
    }

    [Fact]
    public async Task An_unchanged_count_does_not_raise_a_notification()
    {
        var session = SharedUi.Session;
        var state = await session.Dispatch(
            () =>
            {
                using var instruments = Create(openInspector: () => { });
                var raised = new List<string?>();
                instruments.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

                // The session pushes the same count on every tick; only a real move may cost a notification.
                instruments.NoteRunningResources(2);
                var first = raised.ToArray();
                instruments.NoteRunningResources(2);
                var afterSame = raised.ToArray();
                instruments.NoteRunningResources(0);
                return (First: first, AfterSame: afterSame, AfterChange: raised.ToArray());
            },
            CancellationToken.None);

        string?[] oneChange =
        [
            nameof(SessionInstrumentsViewModel.RunningResources),
            nameof(SessionInstrumentsViewModel.OpenInspectorMenuText),
        ];
        string?[] twoChanges =
        [
            nameof(SessionInstrumentsViewModel.RunningResources),
            nameof(SessionInstrumentsViewModel.OpenInspectorMenuText),
            nameof(SessionInstrumentsViewModel.RunningResources),
            nameof(SessionInstrumentsViewModel.OpenInspectorMenuText),
        ];
        Assert.Equal(oneChange, state.First);
        Assert.Equal(state.First, state.AfterSame);
        Assert.Equal(twoChanges, state.AfterChange);
    }

    private static SessionInstrumentsViewModel Create(Action? openInspector) =>
        new(
            new SessionInfo("instruments-session", @"C:\work\forge", "instruments", At),
            EmptySessionInstrumentSources.Instance,
            static () => [],
            static () => "model",
            openInspector: openInspector);
}