using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The composer strip's resource chip is a menu, not a click-through: its first entry is the resource window the chip used
/// to open directly, and its second is the session's ledger timeline. The second entry belongs to the host that wired a
/// ledger source - a session without one draws no such row rather than a disabled one.
/// </summary>
[Collection("forge-app-ui")]
public sealed class SessionStripMenuTests
{
    private static readonly SessionInfo Session = new("test-session", @"C:\nowhere", "Test", DateTimeOffset.UnixEpoch);

    private static SessionInstrumentsViewModel Strip(TurnLedgerPageReader? ledger = null) =>
        new(Session, EmptySessionInstrumentSources.Instance, static () => "test-model", sessionLedger: ledger);

    [Fact]
    public Task A_strip_without_a_ledger_offers_only_the_resource_window() => SharedUi.RunAsync(() =>
    {
        using var strip = Strip();
        var menu = SessionView.ResourceMenu(strip, _ => { });

        var item = Assert.Single(menu.Items.OfType<MenuItem>());
        Assert.Equal(InstrumentsStrings.ResourcesCurrent, item.Header);
        Assert.Equal("strip-resources-current", AutomationProperties.GetAutomationId(item));
        // The window's own command is what the entry runs, so its enabled state stays the overlay's business.
        Assert.Same(strip.OpenInspectorCommand, item.Command);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_strip_with_a_ledger_offers_its_timeline_after_the_window() => SharedUi.RunAsync(() =>
    {
        TurnLedgerPageReader read = static (_, _, _) => Task.FromResult(new TurnLedgerPage([], false, 0));
        using var strip = Strip(read);
        TurnLedgerPageReader? opened = null;
        var menu = SessionView.ResourceMenu(strip, source => opened = source);

        var items = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal([InstrumentsStrings.ResourcesCurrent, SessionStrings.SessionLedger.Resolve()], items.Select(item => item.Header));
        Assert.Equal("strip-resources-ledger", AutomationProperties.GetAutomationId(items[1]));

        // The entry's own action is the read the strip holds: one source, one window.
        items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, items[1]));
        Assert.Same(read, opened);
        return Task.CompletedTask;
    });
}
