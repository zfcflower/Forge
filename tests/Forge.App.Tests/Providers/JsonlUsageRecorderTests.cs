using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// Pins the usage log's file behaviour: a line per request, a file per day, silent while the switch is off, and never a failure that
/// reaches a turn.
/// </summary>
public sealed class JsonlUsageRecorderTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 13, 20, 30, 0, TimeSpan.Zero);

    private readonly string root = TestTempFiles.NewDirectory("forge-usage-log-");

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private UsageRecord Record(string? session = "s", string outcome = UsageRecord.Ok) => new(
        At,
        session,
        "anthropic",
        "anthropic",
        "claude-opus-5-5",
        "sdk-cli",
        "auto",
        false,
        outcome,
        200,
        new TokenUsage(1_000, 20, 800, 100),
        42,
        "req_1",
        new ProviderRateLimit(0.42, At.AddHours(2), RateLimitStatus.Allowed, 0.5, At.AddDays(3), RateLimitStatus.Allowed, RateLimitWindow.FiveHour, RateLimitStatus.Rejected)
        {
            Throttle = new ProviderThrottle(50, 43, At.AddMinutes(1), 40_000, 12_300, At.AddMinutes(1)),
        });

    private string SettingsPath => Path.Combine(root, "settings.json");

    private UsageLogSettingsStore Store(bool enabled)
    {
        var store = new UsageLogSettingsStore(SettingsPath);
        store.Save(new UsageLogSettings(enabled));
        return store;
    }

    [Fact]
    public void Off_means_no_file_at_all()
    {
        var recorder = new JsonlUsageRecorder(Path.Combine(root, "usage"), Store(enabled: false));

        recorder.Record(Record());

        Assert.False(Directory.Exists(Path.Combine(root, "usage")), "the disabled log must not even create its directory");
    }

    [Fact]
    public void A_record_lands_as_one_json_line_carrying_what_an_analysis_needs()
    {
        var directory = Path.Combine(root, "usage");
        var recorder = new JsonlUsageRecorder(directory, Store(enabled: true));

        recorder.Record(Record());

        var path = recorder.PathFor(At);
        Assert.Equal(Path.Combine(directory, "usage-20261113.jsonl"), path);
        var lines = File.ReadAllLines(path);
        var line = Assert.Single(lines);
        using var document = JsonDocument.Parse(line);
        var rootElement = document.RootElement;
        Assert.Equal("sdk-cli", rootElement.GetProperty("entrypoint").GetString());
        Assert.Equal("auto", rootElement.GetProperty("cacheTtl").GetString());
        Assert.Equal("ok", rootElement.GetProperty("outcome").GetString());
        Assert.Equal(1_000, rootElement.GetProperty("usage").GetProperty("input").GetInt32());
        Assert.Equal(800, rootElement.GetProperty("usage").GetProperty("cacheRead").GetInt32());
        Assert.Equal(0.42, rootElement.GetProperty("rateLimit").GetProperty("fiveHour").GetDouble());
        Assert.Equal(43, rootElement.GetProperty("rateLimit").GetProperty("throttle").GetProperty("requestRemaining").GetInt32());
        Assert.Equal(42, rootElement.GetProperty("elapsedMs").GetInt64());
        // One line per request, so a second record appends rather than rewriting the file.
        recorder.Record(Record(session: "other"));
        Assert.Equal(2, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void A_different_day_is_a_different_file()
    {
        var recorder = new JsonlUsageRecorder(Path.Combine(root, "usage"), Store(enabled: true));

        recorder.Record(Record());
        recorder.Record(Record() with { At = At.AddDays(1) });

        Assert.Single(File.ReadAllLines(recorder.PathFor(At)));
        Assert.Single(File.ReadAllLines(recorder.PathFor(At.AddDays(1))));
    }

    [Fact]
    public void An_unwritable_destination_is_swallowed()
    {
        // A file where the directory should be: creating the directory fails, and a diagnostic log must not fail a turn for that.
        var blocked = Path.Combine(root, "blocked");
        File.WriteAllText(blocked, "not a directory");
        var recorder = new JsonlUsageRecorder(Path.Combine(blocked, "usage"), Store(enabled: true));

        recorder.Record(Record());
        recorder.Record(Record());

        Assert.False(Directory.Exists(Path.Combine(blocked, "usage")));
    }
}
