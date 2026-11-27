using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.Core.Storage;
using Xunit;
using TextBlock = Avalonia.Controls.TextBlock;

namespace Forge.App.Tests;

/// <summary>
/// The strip's readings that used to mislead: the context gauge labelled an unmeasured session "0%" (unknown is not
/// zero), the cache-lifetime chip stayed hidden until the first request even though the provider's configuration is
/// readable without one, and the two hover lines (the gauge's and the usage chip's) were evaluated once and then described
/// an older session forever. Everything but the last pair is observable on <see cref="SessionInstrumentsViewModel"/>
/// without a view.
/// </summary>
[Collection("forge-app-ui")]
public class InstrumentsReadingTests
{
    /// <summary>The strip's null-object sources with the one reading a reopened session needs before its first request.</summary>
    private sealed class ConfiguredTtlSources(string? ttl) : ISessionInstrumentSources
    {
        private readonly EmptySessionInstrumentSources empty = EmptySessionInstrumentSources.Instance;

        public string? PromptCacheTtl(string? providerId) => ttl;

        public bool McpFailed() => empty.McpFailed();

        public event Action? McpStateChanged
        {
            add { }
            remove { }
        }

        public ModelMetadata ModelMetadata(string modelId, string? providerId = null) => empty.ModelMetadata(modelId, providerId);

        public Task<SessionTurnHistory> TurnHistoryAsync(string sessionId, CancellationToken ct, int? maxTurnExclusive = null) =>
            empty.TurnHistoryAsync(sessionId, ct, maxTurnExclusive);

        public TurnMetricsRecorder? TurnMetricsRecorder => empty.TurnMetricsRecorder;

        public ITurnMetricsStore? TurnMetrics => empty.TurnMetrics;

        public ITurnLedgerStore? TurnLedger => null;
    }

    private static SessionInstrumentsViewModel Strip(string? ttl) => new(
        new SessionInfo("test-session", @"C:\nowhere", "Test", DateTimeOffset.UnixEpoch),
        new ConfiguredTtlSources(ttl),
        static () => "test-model",
        static () => "test-provider");

    [Fact]
    public void An_unmeasured_context_reads_as_unknown_not_zero()
    {
        using var strip = Strip(ttl: null);

        // Before any request: the label says unknown, and the tooltip says it is pending rather than measuring anything.
        Assert.Equal("—", strip.ContextLabel);
        Assert.Equal(InstrumentsStrings.ContextTooltipPending, strip.ContextTooltip);
        Assert.Equal(0, strip.ContextPercent);

        // Once a request reports the numbers, the label carries them; the pending wording is gone.
        strip.NoteContext("root", 1_000, 2_000, 1_600);
        Assert.Equal("1k/2k · 50%", strip.ContextLabel);
        Assert.DoesNotContain(InstrumentsStrings.ContextTooltipPending, strip.ContextTooltip);
    }

    [Fact]
    public void The_summary_counts_this_windows_own_clock_as_turns_end()
    {
        using var strip = Strip(ttl: null);

        // Nothing ended yet: the duration is unknown, not zero.
        Assert.Equal(UsageStrings.UnknownText, strip.UsageDurationText);

        // Two turns this window watched end: 30 seconds and 2 minutes. Their stored rows are excluded from the history
        // read by the cutoff, so the live side is the only place their clock can come from.
        strip.NoteTurnEnded(1, TimeSpan.FromSeconds(30));
        strip.NoteTurnEnded(2, TimeSpan.FromMinutes(2));
        Assert.Equal("2 分 30 秒", strip.UsageDurationText);
        Assert.Equal("2", strip.UsageTurnsText);

        // A turn that never measured a clock counts as a turn and adds nothing to the duration.
        strip.NoteTurnEnded(3);
        Assert.Equal("2 分 30 秒", strip.UsageDurationText);
        Assert.Equal("3", strip.UsageTurnsText);
    }

    [Fact]
    public void The_cache_lifetime_chip_shows_the_configuration_before_any_request_and_keeps_the_measured_value()
    {
        using var strip = Strip(ttl: "5m");

        // A freshly opened session answers from the provider's config - no first request needed.
        Assert.True(strip.HasCacheTtl);
        Assert.Contains("5m", strip.CacheTtlText);

        // The first request reports the ttl that actually went out, and that value owns the chip from here on.
        strip.NoteCacheTtl("1h");
        Assert.Contains("1h", strip.CacheTtlText);

        // A later counter refresh re-reads the configuration, but never over what a request measured.
        strip.RefreshCounters();
        Assert.Contains("1h", strip.CacheTtlText);
    }

    [Fact]
    public void A_provider_without_a_configured_lifetime_shows_no_chip_before_any_request()
    {
        using var strip = Strip(ttl: null);
        Assert.False(strip.HasCacheTtl);
        Assert.Equal(string.Empty, strip.CacheTtlText);
    }

    /// <summary>
    /// Every hover line of the readings is the reading itself, rebuilt after the number under it moves: the gauge's line
    /// is composed by the control (the numbers plus what a click does on this host), and the usage chip's line is the
    /// session's cumulative token line - the one the group already carried. Both hosts render this control, so both read
    /// the same two lines.
    /// </summary>
    [Fact]
    public Task Every_hover_line_follows_the_reading_it_describes() => SharedUi.RunAsync(() =>
    {
        using var strip = Strip(ttl: null);
        var compact = new RelayCommand(static () => { });
        var view = new InstrumentReadingsView { DataContext = strip, CompactCommand = compact };
        var window = new Window { Width = 900, Height = 200, Content = view };
        window.Show();
        SharedUi.Settle(window);
        var gauge = ByAutomationId(view, "strip-context-compact");
        var chip = ByAutomationId(view, "strip-usage-summary");

        // Before the first request the gauge says the measurement is pending; the strip host's click is promised.
        Assert.Equal(
            InstrumentsStrings.ContextTooltipPending + "\n" + InstrumentsStrings.ContextCompactHint,
            TipText(gauge));

        // A measurement arrives: the line carries it, not the pending wording.
        strip.NoteContext("root", 62_000, 136_000, 115_600);
        SharedUi.Settle(window);
        Assert.Equal(strip.ContextTooltip + "\n" + InstrumentsStrings.ContextCompactHint, TipText(gauge));
        Assert.Contains("62k", TipText(gauge));

        // The other host has no manual compaction, and the line says so instead of promising a click.
        view.CompactCommand = null;
        SharedUi.Settle(window);
        Assert.Equal(strip.ContextTooltip + "\n" + InstrumentsStrings.ContextAutoHint, TipText(gauge));

        // The usage chip's hover line is the cumulative token line, so a finished turn shows up in it.
        Assert.Equal(strip.UsageTooltip, TipText(chip));
        strip.AddTurn("root", new TokenUsage(1_200, 300, 1_000, 0, 0));
        SharedUi.Settle(window);
        Assert.Equal(strip.UsageTooltip, TipText(chip));
        Assert.Contains("↑1.2k ↓300", TipText(chip));

        window.Close();
        return Task.CompletedTask;
    });

    /// <summary>The control the automation id names, as the tests' handle on the two hover lines.</summary>
    private static Control ByAutomationId(Control root, string id) =>
        root.GetVisualDescendants().OfType<Control>().First(c => AutomationProperties.GetAutomationId(c) == id);

    /// <summary>The tip's text: a plain string tip, or the block this control owns and rewrites.</summary>
    private static string TipText(Control control) => ToolTip.GetTip(control) switch
    {
        string text => text,
        TextBlock block => block.Text ?? string.Empty,
        { } other => other.ToString() ?? string.Empty,
        _ => string.Empty,
    };
}
