using System.Text;
using Forge.Abstractions;
using Forge.Core.Providers;
using Xunit;

namespace Forge.Core.Tests.Providers;

/// <summary>
/// Pins the on-disk catalog: a compact JSON cache that survives a round trip, tolerates fields it does not know, and
/// reports damage (so the app falls back to "no catalog" rather than failing to start).
/// </summary>
public sealed class ModelCatalogJsonTests
{
    private static readonly DateTimeOffset FetchedAt = new(2026, 11, 12, 5, 0, 0, TimeSpan.Zero);

    private static ModelCatalogSnapshot Sample() => new(
        "https://models.dev/api.json",
        "\"v1\"",
        FetchedAt,
        new Dictionary<string, IReadOnlyDictionary<string, ModelCatalogModel>>(StringComparer.Ordinal)
        {
            ["anthropic"] = new Dictionary<string, ModelCatalogModel>(StringComparer.Ordinal)
            {
                ["claude-sonnet-4-5"] = new(
                    200_000,
                    64_000,
                    new ModelPricing(3, 15, 0.3, 3.75),
                    ModelInputs.Text | ModelInputs.Image | ModelInputs.Pdf,
                    [ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.High]),
                ["claude-haiku-4-5"] = new(200_000, null, null, null, [ThinkingEffort.Off]),
            },
            ["mystery"] = new Dictionary<string, ModelCatalogModel>(StringComparer.Ordinal)
            {
                ["weights-only"] = new(null, null, null, null, null),
            },
        });

    [Fact]
    public void Everything_written_reads_back_unchanged()
    {
        var original = Sample();

        Assert.True(ModelCatalogJson.TryRead(ModelCatalogJson.Write(original), out var read, out var error));
        Assert.Null(error);
        Assert.NotNull(read);
        Assert.Equal(original.SourceUrl, read!.SourceUrl);
        Assert.Equal(original.ETag, read.ETag);
        Assert.Equal(original.FetchedAt, read.FetchedAt);

        var sonnet = read.Providers["anthropic"]["claude-sonnet-4-5"];
        Assert.Equal(200_000, sonnet.ContextWindow);
        Assert.Equal(64_000, sonnet.MaxOutputTokens);
        Assert.Equal(new ModelPricing(3, 15, 0.3, 3.75), sonnet.Pricing);
        Assert.Equal(ModelInputs.Text | ModelInputs.Image | ModelInputs.Pdf, sonnet.Inputs);
        Assert.Equal([ThinkingEffort.Off, ThinkingEffort.Low, ThinkingEffort.High], sonnet.ReasoningSteps);
        Assert.Equal([ThinkingEffort.Off], read.Providers["anthropic"]["claude-haiku-4-5"].ReasoningSteps);
    }

    [Fact]
    public void An_entry_that_knows_nothing_is_not_written()
    {
        var payload = ModelCatalogJson.Write(Sample());
        var text = Encoding.UTF8.GetString(payload);

        Assert.DoesNotContain("weights-only", text, StringComparison.Ordinal);
        Assert.True(ModelCatalogJson.TryRead(payload, out var read, out _));
        Assert.False(read!.Providers.ContainsKey("mystery"));
    }

    [Fact]
    public void A_missing_etag_round_trips_as_null()
    {
        var snapshot = Sample() with { ETag = null };

        Assert.True(ModelCatalogJson.TryRead(ModelCatalogJson.Write(snapshot), out var read, out _));
        Assert.Null(read!.ETag);
    }

    [Fact]
    public void Fields_this_build_does_not_know_are_ignored()
    {
        var payload = Encoding.UTF8.GetBytes("""
        {"version":1,"sourceUrl":"u","etag":null,"fetchedAt":"2026-11-12T05:00:00.0000000+00:00","future":42,
         "providers":{"anthropic":{"claude-sonnet-4-5":{"context":200000,"maxOutput":64000,"inputs":["text","image"],
         "reasoning":true,"efforts":["off","high"],"somethingNew":"x"}}}}
        """);

        Assert.True(ModelCatalogJson.TryRead(payload, out var read, out var error));
        Assert.Null(error);
        var sonnet = read!.Providers["anthropic"]["claude-sonnet-4-5"];
        Assert.Equal(ModelInputs.Text | ModelInputs.Image, sonnet.Inputs);
        Assert.Equal([ThinkingEffort.Off, ThinkingEffort.High], sonnet.ReasoningSteps);
    }

    [Theory]
    [InlineData("{\"version\":2,\"fetchedAt\":\"2026-11-12T05:00:00+00:00\",\"providers\":{}}", "version")]
    [InlineData("{\"version\":1,\"providers\":{}}", "fetchedAt")]
    [InlineData("{\"version\":1,\"fetchedAt\":\"2026-11-12T05:00:00+00:00\"}", "providers")]
    [InlineData("{\"version\":1,", "JSON")]
    [InlineData("[]", "object")]
    public void A_file_this_build_cannot_trust_is_reported_and_read_as_no_catalog(string payload, string expected)
    {
        Assert.False(ModelCatalogJson.TryRead(Encoding.UTF8.GetBytes(payload), out var read, out var error));
        Assert.Null(read);
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_local_date_of_the_fetch_follows_the_time_zone()
    {
        // 23:30 UTC is already the 13th in UTC+2 and still the 12th in UTC-6.
        var snapshot = Sample() with { FetchedAt = new DateTimeOffset(2026, 11, 12, 23, 30, 0, TimeSpan.Zero) };
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        var minusSix = TimeZoneInfo.CreateCustomTimeZone("minus6", TimeSpan.FromHours(-6), "minus6", "minus6");

        Assert.Equal(new DateOnly(2026, 11, 13), snapshot.LocalDate(plusTwo));
        Assert.Equal(new DateOnly(2026, 11, 12), snapshot.LocalDate(minusSix));
        Assert.True(snapshot.IsFromDay(new DateOnly(2026, 11, 13), plusTwo));
        Assert.False(snapshot.IsFromDay(new DateOnly(2026, 11, 12), plusTwo));
    }

    [Fact]
    public void A_snapshot_that_was_never_fetched_has_no_date()
    {
        var empty = ModelCatalogSnapshot.Empty("https://models.dev/api.json");

        Assert.Null(empty.LocalDate(TimeZoneInfo.Utc));
        Assert.False(empty.IsFromDay(new DateOnly(2026, 11, 12), TimeZoneInfo.Utc));
        Assert.Equal(0, empty.ModelCount);
    }
}
