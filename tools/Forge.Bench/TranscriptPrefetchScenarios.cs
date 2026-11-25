using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.Core.Storage;

namespace Forge.Bench;

/// <summary>
/// What one "read the page above" costs, split into the parts the prefetch design proposes to remove, plus what landing
/// that page costs the UI thread.
/// <para>The read rows page backwards through a real session store: <c>prefetch.page.scan</c> reads the newest page against
/// the same page at the start of the history (same 100 messages, same metrics read - the difference is the prefix
/// <c>COUNT</c> over the older rows the round numbers are derived from), and <c>prefetch.page.metrics</c> reads the page
/// with and without the session's <c>turn_metrics</c> table (the difference is what reusing the rows read at open would
/// save). Both use <see cref="TranscriptReplay.ReadPageAsync"/>, the path the transcript actually loads older history
/// through.</para>
/// <para><c>prefetch.prepend.*</c> lands one page of 50 rounds above a paused reader on the real transcript list, in three
/// states: <c>today</c> (the design's flow, which lets the follower read the prepend as a sent message), <c>held</c> (the
/// same landing with that flip suppressed - what the design promises) and <c>bringback</c> (the flip suppressed and the old
/// first round brought back into view once, the way today's code-behind does it). Each reports the insert, the first frame
/// after it, the UI-thread allocation, the drift of the round the reader was looking at, and whether the page above is
/// reachable by scrolling (extent, top gap, first realized round).</para>
/// </summary>
internal static partial class TranscriptScenarios
{
    /// <summary>Session sizes the page read is priced at: 2k and 10k stored messages.</summary>
    private static readonly int[] PrefetchTurns = [1_000, 5_000];

    private static readonly Dictionary<int, PrefetchFixture> PrefetchFixtures = [];

    /// <summary>One prepared session: the store page reads go through, its metrics rows, and where the pages start.</summary>
    private sealed record PrefetchFixture(
        SqliteSessionStore Store,
        SqliteTurnMetricsStore Metrics,
        string SessionId,
        long FirstPageBefore,
        long SecondPageBefore,
        long TurnsAboveSecondPage,
        IReadOnlyList<TurnMetric> MetricsRows);

    private static IEnumerable<BenchScenario> PrefetchScenarios(Fixtures fixtures)
    {
        foreach (var turns in PrefetchTurns)
        {
            yield return PrefetchScan(fixtures, turns);
            yield return PrefetchMetrics(fixtures, turns);
            yield return PrefetchPagedPath(fixtures, turns);
        }

        yield return PrependPage("today");
        yield return PrependPage("held");
        yield return PrependPage("bringback");
        yield return PrependPage("held-top");
    }

    /// <summary>
    /// The prefix count: the newest page against the same page at the start of the history. Both read 100 messages and the
    /// session's metrics; the newer one also counts the user messages below it for the transcript's round numbers.
    /// </summary>
    private static BenchScenario PrefetchScan(Fixtures fixtures, int turns) =>
        new(
            $"prefetch.page.scan.{turns}turns",
            "prefetch",
            Heavy: turns >= 5_000,
            PrepareAsync: async cancellationToken =>
            {
                var fixture = await PrefetchFixtureAsync(fixtures, turns, cancellationToken).ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 2,
                    Iterations = 12,
                    ProcessWideAllocation = true,
                    Note = "one page = 100 stored messages, read through TranscriptReplay.ReadPageAsync",
                    Run = cancellationToken2 => ReadPageAsync(fixture, long.MaxValue, withMetrics: true, cancellationToken2),
                    Baseline = cancellationToken2 => ReadPageAsync(fixture, fixture.FirstPageBefore, withMetrics: true, cancellationToken2),
                    BaselineName = "same page at the start of the history (no older rows to count)",
                };
            });

    /// <summary>The session-wide metrics read the page path repeats on every page.</summary>
    private static BenchScenario PrefetchMetrics(Fixtures fixtures, int turns) =>
        new(
            $"prefetch.page.metrics.{turns}turns",
            "prefetch",
            Heavy: turns >= 5_000,
            PrepareAsync: async cancellationToken =>
            {
                var fixture = await PrefetchFixtureAsync(fixtures, turns, cancellationToken).ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 2,
                    Iterations = 12,
                    ProcessWideAllocation = true,
                    Note = "newest page; the difference is the whole session's turn_metrics rows read again",
                    Run = cancellationToken2 => ReadPageAsync(fixture, long.MaxValue, withMetrics: true, cancellationToken2),
                    Baseline = cancellationToken2 => ReadPageAsync(fixture, long.MaxValue, withMetrics: false, cancellationToken2),
                    BaselineName = "the same page without the metrics read (rows cached from the open)",
                };
            });

    /// <summary>
    /// The second page through the code the tab now uses: metrics carried over from the open and the page numbered from the
    /// count above the boundary, against the first version of that read (no metrics passed, no count passed - the store
    /// re-reads the session's whole metrics table and counts the prefix). Prepare checks both produce the same page, so the
    /// comparison is between two ways of reading one page, not two pages.
    /// </summary>
    private static BenchScenario PrefetchPagedPath(Fixtures fixtures, int turns) =>
        new(
            $"prefetch.page.paged.{turns}turns",
            "prefetch",
            Heavy: turns >= 5_000,
            PrepareAsync: async cancellationToken =>
            {
                var fixture = await PrefetchFixtureAsync(fixtures, turns, cancellationToken).ConfigureAwait(false);
                var carried = await ReadSecondPageAsync(fixture, carried: true, cancellationToken).ConfigureAwait(false);
                var counted = await ReadSecondPageAsync(fixture, carried: false, cancellationToken).ConfigureAwait(false);
                if (carried.Page.Messages.Count != counted.Page.Messages.Count
                    || carried.Page.Messages[0].Seq != counted.Page.Messages[0].Seq
                    || carried.Page.TurnsBefore != counted.Page.TurnsBefore
                    || carried.Page.HasMore != counted.Page.HasMore)
                {
                    throw new InvalidOperationException(
                        $"the carried count changed the page: {carried.Page.TurnsBefore} vs {counted.Page.TurnsBefore}, "
                        + $"{carried.Page.Messages.Count} vs {counted.Page.Messages.Count} messages");
                }

                return new BenchCase
                {
                    Warmup = 2,
                    Iterations = 12,
                    ProcessWideAllocation = true,
                    Note = "the page below the newest one: carried metrics + the count above the boundary vs the counted read",
                    Run = async cancellationToken2 =>
                    {
                        var load = await ReadSecondPageAsync(fixture, carried: true, cancellationToken2).ConfigureAwait(false);
                        return new OpResult(
                            Items: load.Page.Messages.Count,
                            Observation: string.Create(
                                CultureInfo.InvariantCulture,
                                $"{load.Metrics.Count} metric rows, turnsBefore {load.Page.TurnsBefore}, hasMore {load.Page.HasMore}"));
                    },
                    Baseline = async cancellationToken2 =>
                    {
                        var load = await ReadSecondPageAsync(fixture, carried: false, cancellationToken2).ConfigureAwait(false);
                        return new OpResult(Items: load.Page.Messages.Count);
                    },
                    BaselineName = "the same page as the first paged read ran it (whole metrics table + prefix count)",
                };
            });

    /// <summary>The page below the newest one, read the way the tab does it now or the way it did before.</summary>
    private static Task<TranscriptReplay.Load> ReadSecondPageAsync(
        PrefetchFixture fixture, bool carried, CancellationToken cancellationToken) =>
        TranscriptReplay.ReadPageAsync(
            fixture.Store,
            carried ? null : fixture.Metrics,
            fixture.SessionId,
            fixture.SecondPageBefore,
            100,
            cancellationToken,
            carried ? fixture.MetricsRows : null,
            carried ? fixture.TurnsAboveSecondPage : null);

    /// <summary>One page (50 rounds, what 100 messages replay into) landing above a paused reader.</summary>
    private static BenchScenario PrependPage(string mode) =>
        new(
            $"prefetch.prepend.{mode}",
            "prefetch",
            Heavy: false,
            PrepareAsync: async cancellationToken =>
            {
                var rig = await Rig.GetAsync(cancellationToken).ConfigureAwait(false);
                return new BenchCase
                {
                    Warmup = 0,
                    Iterations = 3,
                    ProcessWideAllocation = true,
                    Note = mode switch
                    {
                        "today" => "as the design would leave it (strip gone, no ScrollIntoView): insert + first frame for 50 rounds above the reader",
                        "held" => "the same landing with the follower's flip suppressed right after the insert: what the design promises",
                        "held-top" => "the design's own case: the reader is at the top of the loaded history when the page lands",
                        _ => "the flip suppressed and the old first round brought back into view once, the way today's code-behind does it",
                    },
                    Run = async _ =>
                    {
                        var report = await rig.PrependPageAsync(mode).ConfigureAwait(false);
                        Console.Error.WriteLine($"prefetch prepend.{mode}: {report}");
                        return new OpResult(Items: 50, Observation: report);
                    },
                    // The landing the design promises: the reader's round stays put and following stays off. Today's flow
                    // fails both (see the "today" row), which is why this is the acceptance for the fix, not a nicety.
                    Check = mode != "held"
                        ? null
                        : comparison =>
                        {
                            var note = comparison.Implementation.Observation ?? string.Empty;
                            var drift = System.Text.RegularExpressions.Regex.Match(note, @"drift (-?\d+(?:\.\d+)?) DIP");
                            var held = note.Contains("following False->False", StringComparison.Ordinal)
                                && drift.Success
                                && Math.Abs(double.Parse(drift.Groups[1].Value, CultureInfo.InvariantCulture)) <= 1;
                            return new Verdict("a prepended page holds the reader (drift <= 1 DIP, following stays off)", note, held);
                        },
                };
            });

    /// <summary>One page read exactly as the tab's older-history path runs it.</summary>
    private static async Task<OpResult> ReadPageAsync(
        PrefetchFixture fixture,
        long beforeSeq,
        bool withMetrics,
        CancellationToken cancellationToken)
    {
        var load = await TranscriptReplay
            .ReadPageAsync(
                fixture.Store,
                withMetrics ? fixture.Metrics : null,
                fixture.SessionId,
                beforeSeq,
                100,
                cancellationToken)
            .ConfigureAwait(false);
        if (load.Page.Messages.Count != 100)
        {
            throw new InvalidOperationException($"expected a full 100-message page, read {load.Page.Messages.Count}");
        }

        return new OpResult(
            Items: load.Page.Messages.Count,
            Observation: string.Create(
                CultureInfo.InvariantCulture,
                $"{load.Metrics.Count} metric rows, turnsBefore {load.Page.TurnsBefore}, hasMore {load.Page.HasMore}"));
    }

    /// <summary>
    /// A session of <paramref name="turns"/> question/answer pairs with one metrics row per turn - the shape a real
    /// conversation has, so the page read pays for both tables the way it does in the app.
    /// </summary>
    private static async Task<PrefetchFixture> PrefetchFixtureAsync(
        Fixtures fixtures,
        int turns,
        CancellationToken cancellationToken)
    {
        if (PrefetchFixtures.TryGetValue(turns, out var ready))
        {
            return ready;
        }

        var directory = fixtures.Workspace.DirectoryAt($"prefetch/{turns}");
        var store = new SqliteSessionStore(directory);
        var session = await store.CreateAsync(new SessionCreate(directory, $"prefetch-{turns}"), cancellationToken)
            .ConfigureAwait(false);
        var metrics = new SqliteTurnMetricsStore(directory);
        var question = new string('q', 400);
        var answer = new string('a', 1_200);
        var started = DateTimeOffset.UtcNow;
        long lastSeq = 0;
        for (var turn = 1; turn <= turns; turn++)
        {
            await store.AppendAsync(session.Id, ChatMessage.User(question), null, cancellationToken).ConfigureAwait(false);
            lastSeq = await store.AppendAsync(
                    session.Id,
                    ChatMessage.Assistant(answer),
                    new TokenUsage(100, 50),
                    cancellationToken)
                .ConfigureAwait(false);
            await metrics.WriteAsync(
                    new TurnMetric(
                        session.Id,
                        turn,
                        started.AddSeconds(turn * 4),
                        started.AddSeconds((turn * 4) + 3),
                        TurnMetricStatus.Completed,
                        Provider: "bench",
                        Model: "bench/model-a",
                        InputTokens: 900,
                        OutputTokens: 300,
                        CacheReadTokens: 400,
                        CacheWriteTokens: 120,
                        CacheWrite1hTokens: 0,
                        ToolCalls: 2,
                        StreamElapsed: TimeSpan.FromSeconds(2),
                        LastSeq: lastSeq),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // The second page is the one a paging tab reads after its first: below the newest page's oldest message, numbered
        // from the count above it.
        var newest = await store.ReadTailAsync(session.Id, long.MaxValue, 100, cancellationToken).ConfigureAwait(false);
        var metricsRows = await metrics.ListAsync(session.Id, cancellationToken).ConfigureAwait(false);

        // The page the read row uses at depth 0: messages 101..200, so only those 100 rows precede it.
        var fixture = new PrefetchFixture(
            store,
            metrics,
            session.Id,
            FirstPageBefore: 201,
            SecondPageBefore: newest.Messages[0].Seq,
            TurnsAboveSecondPage: newest.TurnsBefore,
            MetricsRows: metricsRows);
        PrefetchFixtures[turns] = fixture;
        return fixture;
    }

    private sealed partial class Rig
    {
        /// <summary>
        /// Lands one page of 50 rounds above a paused reader and reports what that cost and what it did to the place the
        /// reader was at: the insert, the frame after it, the watched round's drift, whether following came back, and
        /// whether the page above became reachable at all (the extent, before and after a wheel-sized scroll up).
        /// </summary>
        public Task<string> PrependPageAsync(string mode) =>
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var transcript = BuildRich(120, false);
                var probe = await MountAsync(transcript, pinned: true).ConfigureAwait(true);
                try
                {
                    probe.Follower.StopFollowing();
                    if (mode == "held-top")
                    {
                        probe.Scroller.Offset = new Vector(0, 0);
                    }
                    else
                    {
                        await PlaceAsync(probe, Tool(transcript, 100, 0), 60).ConfigureAwait(true);
                    }

                    await FramesAsync(2).ConfigureAwait(true);
                    // At the very top the first round starts at the list's padding, so nothing crosses the top edge:
                    // watch the round the reader is looking at instead (the first one).
                    var watched = mode == "held-top"
                        ? transcript.Rounds[0]
                        : (TranscriptRoundViewModel)TopRow(probe);
                    var page = BuildRich(50, false);
                    var before = RoundY(probe, watched);
                    var offset = probe.Scroller.Offset.Y;
                    var extent = probe.Scroller.Extent.Height;
                    var followingBefore = probe.Follower.IsFollowing;
                    probe.OffsetWrites = 0;
                    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    var started = Stopwatch.StartNew();
                    transcript.PrependRounds(page);
                    if (mode != "today")
                    {
                        // The design's rule, applied by hand: a prepend is not the user sending a message.
                        probe.Follower.StopFollowing();
                    }

                    var insertMs = started.Elapsed.TotalMilliseconds;
                    await NextFrameAsync().ConfigureAwait(true);
                    var frameMs = started.Elapsed.TotalMilliseconds;
                    if (mode == "bringback")
                    {
                        // What today's code-behind does after the read: scroll the round that was first back to the top.
                        probe.List.ScrollIntoView(transcript.Rounds.IndexOf(watched));
                    }

                    await FramesAsync(2).ConfigureAwait(true);
                    var allocatedKb = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1024d;
                    var scroller = probe.Scroller;
                    var landed = scroller.Offset.Y;
                    var landedExtent = scroller.Extent.Height;
                    var after = RoundY(probe, watched);
                    var drift = before is { } from && after is { } to
                        ? (to - from).ToString("F1", CultureInfo.InvariantCulture)
                        : "lost";
                    var atBottom = landed >= landedExtent - scroller.Viewport.Height - 2;

                    // Forty wheel notches up: does the page that just landed above reach the extent and the screen?
                    var wheelStart = scroller.Offset.Y;
                    var lastOffset = scroller.Offset.Y;
                    var worstJump = 0d;
                    var worstNotchMs = 0d;
                    var worstNotchAt = -1;
                    var slowNotches = 0;
                    var notches = 0;
                    for (var notch = 0; notch < 1_200; notch++)
                    {
                        scroller.Offset = new Vector(0, Math.Max(0, scroller.Offset.Y - 48));
                        var notchStarted = Stopwatch.GetTimestamp();
                        window.UpdateLayout();
                        var notchMs = Stopwatch.GetElapsedTime(notchStarted).TotalMilliseconds;
                        if (notchMs > worstNotchMs)
                        {
                            worstNotchMs = notchMs;
                            worstNotchAt = notch;
                        }

                        slowNotches += notchMs > 16 ? 1 : 0;
                        notches++;
                        var moved = Math.Abs(lastOffset - scroller.Offset.Y);
                        worstJump = Math.Max(worstJump, moved - 48);
                        lastOffset = scroller.Offset.Y;
                        if (lastOffset <= 0)
                        {
                            break;
                        }
                    }

                    await FramesAsync(2).ConfigureAwait(true);
                    var wheelOffset = scroller.Offset.Y;
                    var wheelExtent = scroller.Extent.Height;
                    var gap = TopGap(probe);
                    var climbFirst = probe.List.GetRealizedContainers()
                        .Select(container => probe.List.IndexFromContainer(container))
                        .Where(index => index >= 0)
                        .DefaultIfEmpty(-1)
                        .Min();

                    // All the way to the top of the scroll range: is the page that landed above in it at all?
                    scroller.Offset = new Vector(0, 0);
                    await FramesAsync(2).ConfigureAwait(true);
                    var firstRealized = probe.List.GetRealizedContainers()
                        .Select(container => probe.List.IndexFromContainer(container))
                        .Where(index => index >= 0)
                        .DefaultIfEmpty(-1)
                        .Min();
                    var extentAtZero = scroller.Extent.Height;
                    var gapAtZero = TopGap(probe);
                    return string.Create(
                        CultureInfo.InvariantCulture,
                        $"insert {insertMs:F2} ms + first frame {frameMs:F2} ms, {allocatedKb:F0} KB on the UI thread; drift {drift} DIP; offset {offset:F0}->{landed:F0} after the landing (bottom {atBottom}), extent {extent:F0}->{landedExtent:F0}, viewport {scroller.Viewport.Height:F0}; offset writes {probe.OffsetWrites}; following {followingBefore}->{probe.Follower.IsFollowing}; {transcript.Rounds.Count} rounds; wheel climb ({notches} notches): offset {wheelStart:F0}->{wheelOffset:F0}, worst extra movement {worstJump:F0} DIP, slowest notch {worstNotchMs:F1} ms at #{worstNotchAt}, {slowNotches} over 16 ms, extent {landedExtent:F0}->{wheelExtent:F0}, first realized round #{climbFirst}, top gap {gap:F0} DIP; at offset 0: extent {extentAtZero:F0}, first realized round #{firstRealized}, top gap {gapAtZero:F0} DIP");
                }
                finally
                {
                    window.Content = null;
                }
            });
    }
}
