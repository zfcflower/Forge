using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// One model as the fetched catalog describes it: window, prices and what the model accepts. Every part is optional because
/// the upstream catalog omits what it does not know (<c>cost</c> is missing for 394 of 8 993 entries today).
/// </summary>
/// <param name="ContextWindow">The context window in tokens.</param>
/// <param name="MaxOutputTokens">The largest single reply the model accepts.</param>
/// <param name="Pricing">List prices per one million tokens; cache paths the source omits are zero.</param>
/// <param name="Inputs">The content kinds the model takes as input, or <c>null</c> when the source listed none.</param>
/// <param name="ReasoningSteps">
/// The reasoning steps the source says the model accepts, ascending from <see cref="ThinkingEffort.Off"/>; <c>null</c> when
/// it said nothing usable (a plain on/off toggle is not a set of steps).
/// </param>
public sealed record ModelCatalogModel(
    int? ContextWindow,
    int? MaxOutputTokens,
    ModelPricing? Pricing,
    ModelInputs? Inputs,
    IReadOnlyList<ThinkingEffort>? ReasoningSteps)
{
    /// <summary>
    /// Whether the source says the model reasons, even when it lists no steps (<c>reasoning: true</c> with an on/off
    /// toggle). <c>null</c> is unknown.
    /// </summary>
    public bool? Reasoning { get; init; }

    /// <summary>The capability half, or <c>null</c> when the source described none of it.</summary>
    public ModelCapabilities? Capabilities =>
        Inputs is null && ReasoningSteps is null && Reasoning is null
            ? null
            : new ModelCapabilities(Inputs, ReasoningSteps) { Reasoning = Reasoning };

    /// <summary>
    /// The same facts as <see cref="ModelMetadata"/>, so the catalog layer can be merged into the resolution chain: the
    /// context window, the prices and the capabilities, with the reply limit left out because nothing prices it.
    /// </summary>
    public ModelMetadata ToMetadata() => new(ContextWindow, Pricing) { Capabilities = Capabilities };
}

/// <summary>
/// The compact catalog the daily refresh keeps on disk: <c>model-catalog.json</c> beside <c>settings.json</c>. It holds only
/// what the app uses (window, prices, input kinds, reasoning steps) instead of the 6 MB upstream payload, and it records
/// where it came from and when, so "is today's catalog already here?" is a file read rather than a network round trip.
/// </summary>
/// <param name="SourceUrl">The URL this snapshot was fetched from; a snapshot whose URL differs from the configured one is still used until the new source answers.</param>
/// <param name="ETag">The validator the source answered with, sent back as <c>If-None-Match</c> so an unchanged catalog costs a 304.</param>
/// <param name="FetchedAt">When this snapshot was fetched (UTC). The daily refresh compares its local date with today's.</param>
/// <param name="Providers">Provider id → model id → the model.</param>
public sealed record ModelCatalogSnapshot(
    string SourceUrl,
    string? ETag,
    DateTimeOffset FetchedAt,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, ModelCatalogModel>> Providers)
{
    /// <summary>
    /// When this source last answered "unchanged" (a 304), or <c>null</c> when the last answer delivered this snapshot.
    /// It exists because "is today's catalog here?" must be answered by the last check, not the last change — otherwise a
    /// source that answers 304 for a day would make every startup fetch again (and record a run) while the app still had
    /// the newest catalog.
    /// </summary>
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>When the source was last asked: <see cref="CheckedAt"/> when a check changed nothing, else <see cref="FetchedAt"/>.</summary>
    public DateTimeOffset LastCheck => CheckedAt ?? FetchedAt;

    /// <summary>No catalog fetched yet: what a fresh install has until the first refresh finishes.</summary>
    public static ModelCatalogSnapshot Empty(string sourceUrl) => new(
        sourceUrl,
        null,
        DateTimeOffset.MinValue,
        new Dictionary<string, IReadOnlyDictionary<string, ModelCatalogModel>>(StringComparer.Ordinal));

    /// <summary>How many models the snapshot describes (the settings line shows it).</summary>
    public int ModelCount
    {
        get
        {
            var count = 0;
            foreach (var models in Providers.Values)
            {
                count += models.Count;
            }

            return count;
        }
    }

    /// <summary>True when the source was last asked on <paramref name="today"/>'s local date in <paramref name="zone"/>.</summary>
    public bool IsFromDay(DateOnly today, TimeZoneInfo zone) =>
        LocalDate(zone) == today;

    /// <summary>The local date of the last check, or <c>null</c> when the source was never asked.</summary>
    public DateOnly? LocalDate(TimeZoneInfo zone) =>
        LastCheck == DateTimeOffset.MinValue
            ? null
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(LastCheck, zone).DateTime);
}
