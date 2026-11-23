using Forge.Abstractions;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The strip's reading follows a turn as it spends: every model request reports the instance's cumulative counters, and the
/// reading takes only the difference, so a turn with three requests shows three upgrades instead of one jump at its end. Each
/// observation is counted exactly once — the completion event that closes the turn, and a late reading of the same counter,
/// add nothing — because the subscription is the difference to what the reading was already told, never the raw figure.
/// </summary>
public sealed class SessionLiveUsageTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-live-usage-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient http = new();

    public void Dispose()
    {
        http.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then.
        }
    }

    [Fact]
    public void Every_observation_moves_the_reading_by_its_own_difference_once()
    {
        Directory.CreateDirectory(directory);
        using var session = Session();
        var strip = session.Instruments;

        // One request's worth: the chip reads it at once, not at the end of the turn.
        session.NoteLiveUsage("root", new TokenUsage(1_200, 300, 1_000, 0, 0));
        Assert.Equal("↑1.2k ↓300 · RC 83.3%", strip.UsageChipText);

        // The same cumulative counter observed again (a compaction's measurement, a re-report) adds nothing.
        session.NoteLiveUsage("root", new TokenUsage(1_200, 300, 1_000, 0, 0));
        Assert.Equal("↑1.2k ↓300 · RC 83.3%", strip.UsageChipText);

        // The next request moved the counter: only the difference lands.
        session.NoteLiveUsage("root", new TokenUsage(1_500, 340, 1_300, 0, 0));
        Assert.Equal("↑1.5k ↓340 · RC 86.7%", strip.UsageChipText);

        // Another agent's counters are not this strip's reading.
        session.NoteLiveUsage("worker-1", new TokenUsage(9_000, 9_000, 0, 0, 0));
        Assert.Equal("↑1.5k ↓340 · RC 86.7%", strip.UsageChipText);

        // A measurement that reports nothing at all (a compaction, a request nobody metered) moves nothing, and it must not
        // reset the baseline either: the next real observation still counts from what the strip already had.
        session.NoteLiveUsage("root", TokenUsage.Empty);
        Assert.Equal("↑1.5k ↓340 · RC 86.7%", strip.UsageChipText);
        session.NoteLiveUsage("root", new TokenUsage(1_600, 360, 1_300, 0, 0));
        Assert.Equal("↑1.6k ↓360 · RC 81.3%", strip.UsageChipText);
    }

    /// <summary>The tab as a plain view: no runtime, so no turn drives it - the test feeds the readings itself.</summary>
    private SessionWorkspaceViewModel Session() => new(
        new SessionInfo("live-usage-session", directory, "Live usage", DateTimeOffset.UnixEpoch),
        runtime: null,
        checkpoints: null,
        store: new SqliteSessionStore(directory),
        new ProviderServices(Path.Combine(directory, "settings.json"), directory, http));
}
