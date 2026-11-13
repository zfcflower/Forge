using System.Text;
using Xunit;

namespace Forge.IO.Tests;

/// <summary>
/// The rolling output tail behind <c>ProcessRequest.OnOutput</c>, driven with a manually advanced clock and a manually fired
/// timer so every interval rule is deterministic: the first chunk reports at once, a chunk inside the interval waits for the
/// one-shot timer, the buffer keeps only the newest characters without splitting a surrogate pair, each stream decodes with
/// its own decoder, and a disposed or failing tail never reports late or breaks its caller.
/// </summary>
public class ProcessOutputTailTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void The_first_chunk_is_reported_immediately_without_arming_the_timer()
    {
        var harness = new TailHarness(capacity: 100, Interval);

        harness.Append(harness.NewDecoder(), "hello");

        Assert.Equal(["hello"], harness.Reports);
        Assert.Empty(harness.Timer.Changes);
    }

    [Fact]
    public void A_chunk_inside_the_interval_waits_for_the_timer_and_then_reports_the_merged_text()
    {
        var harness = new TailHarness(capacity: 100, Interval);
        var decoder = harness.NewDecoder();
        harness.Append(decoder, "a");

        harness.Clock.Advance(100);
        harness.Append(decoder, "b");

        Assert.Equal(["a"], harness.Reports);
        Assert.Equal([TimeSpan.FromMilliseconds(150)], harness.Timer.Changes.Select(change => change.Due).ToArray());

        harness.Clock.Advance(150);
        harness.Timer.Fire();

        Assert.Equal(["a", "ab"], harness.Reports);
    }

    [Fact]
    public void Several_chunks_inside_one_interval_arm_the_timer_once_and_report_once()
    {
        var harness = new TailHarness(capacity: 100, Interval);
        var decoder = harness.NewDecoder();
        harness.Append(decoder, "a");

        harness.Clock.Advance(10);
        harness.Append(decoder, "b");
        harness.Clock.Advance(10);
        harness.Append(decoder, "c");
        harness.Clock.Advance(10);
        harness.Append(decoder, "d");

        Assert.Single(harness.Timer.Changes);
        harness.Clock.Advance(220);
        harness.Timer.Fire();

        Assert.Equal(["a", "abcd"], harness.Reports);
    }

    [Fact]
    public void A_report_from_the_timer_restarts_the_interval_and_a_later_chunk_arms_the_timer_again()
    {
        var harness = new TailHarness(capacity: 100, Interval);
        var decoder = harness.NewDecoder();
        harness.Append(decoder, "a");
        harness.Clock.Advance(100);
        harness.Append(decoder, "b");
        harness.Clock.Advance(150);
        harness.Timer.Fire();

        harness.Clock.Advance(10);
        harness.Append(decoder, "c");

        Assert.Equal(["a", "ab"], harness.Reports);
        Assert.Equal(2, harness.Timer.Changes.Count);

        harness.Clock.Advance(240);
        harness.Timer.Fire();

        Assert.Equal(["a", "ab", "abc"], harness.Reports);
    }

    [Fact]
    public void A_chunk_after_the_interval_has_passed_reports_immediately_again()
    {
        var harness = new TailHarness(capacity: 100, Interval);
        var decoder = harness.NewDecoder();
        harness.Append(decoder, "a");

        harness.Clock.Advance(250);
        harness.Append(decoder, "b");

        Assert.Equal(["a", "ab"], harness.Reports);
        Assert.Empty(harness.Timer.Changes);
    }

    [Fact]
    public void A_timer_that_fires_with_nothing_pending_reports_nothing()
    {
        var harness = new TailHarness(capacity: 100, Interval);
        harness.Append(harness.NewDecoder(), "a");

        harness.Clock.Advance(500);
        harness.Timer.Fire();

        Assert.Equal(["a"], harness.Reports);
    }

    [Fact]
    public void Only_the_newest_characters_up_to_the_capacity_are_kept()
    {
        var harness = new TailHarness(capacity: 5, TimeSpan.Zero);

        harness.Append(harness.NewDecoder(), "abcdefgh");

        Assert.Equal(["defgh"], harness.Reports);
    }

    [Fact]
    public void Trimming_applies_across_chunks_and_keeps_the_newest_characters()
    {
        var harness = new TailHarness(capacity: 5, TimeSpan.Zero);
        var decoder = harness.NewDecoder();

        harness.Append(decoder, "abc");
        harness.Append(decoder, "def");
        harness.Append(decoder, "g");

        Assert.Equal(["abc", "bcdef", "cdefg"], harness.Reports);
    }

    [Fact]
    public void A_capacity_below_one_still_keeps_the_last_character()
    {
        var harness = new TailHarness(capacity: 0, TimeSpan.Zero);

        harness.Append(harness.NewDecoder(), "xyz");

        Assert.Equal(["z"], harness.Reports);
    }

    [Fact]
    public void A_cut_that_would_leave_a_lone_low_surrogate_at_the_front_drops_the_whole_pair()
    {
        // The text is high, low, b, c. Capacity 3 cuts one character, which would leave the low surrogate in front.
        var harness = new TailHarness(capacity: 3, TimeSpan.Zero);

        harness.Append(harness.NewDecoder(), "\U0001F600bc");

        var tail = Assert.Single(harness.Reports);
        Assert.Equal("bc", tail);
        Assert.False(char.IsLowSurrogate(tail[0]));
    }

    [Fact]
    public void A_surrogate_pair_that_fits_exactly_at_the_front_is_kept_whole()
    {
        var harness = new TailHarness(capacity: 3, TimeSpan.Zero);

        harness.Append(harness.NewDecoder(), "a\U0001F600b");

        Assert.Equal(["\U0001F600b"], harness.Reports);
    }

    [Fact]
    public void A_three_byte_character_split_across_two_appends_decodes_intact()
    {
        var euro = Utf8("€");
        var harness = new TailHarness(capacity: 100, TimeSpan.Zero);
        var decoder = harness.NewDecoder();

        harness.Append(decoder, euro.AsSpan(0, 2));
        harness.Append(decoder, euro.AsSpan(2));

        // The incomplete first half produces no characters, hence no report and no replacement character.
        Assert.Equal(["€"], harness.Reports);
    }

    [Fact]
    public void A_character_split_one_byte_at_a_time_decodes_intact_after_earlier_complete_text()
    {
        var bytes = Utf8("a€b");
        var harness = new TailHarness(capacity: 100, TimeSpan.Zero);
        var decoder = harness.NewDecoder();

        for (var index = 0; index < bytes.Length; index++)
        {
            harness.Append(decoder, bytes.AsSpan(index, 1));
        }

        Assert.Equal("a€b", harness.Reports[^1]);
        Assert.DoesNotContain('�', harness.Reports[^1]);
    }

    [Fact]
    public void Each_stream_keeps_its_own_decoder_state_while_their_text_interleaves_in_arrival_order()
    {
        var euro = Utf8("€");
        var harness = new TailHarness(capacity: 100, TimeSpan.Zero);
        var stdout = harness.NewDecoder();
        var stderr = harness.NewDecoder();

        harness.Append(stdout, euro.AsSpan(0, 2));
        harness.Append(stderr, Utf8("err"));
        harness.Append(stdout, euro.AsSpan(2));

        Assert.Equal("err€", harness.Reports[^1]);
        Assert.DoesNotContain('�', harness.Reports[^1]);
    }

    [Fact]
    public void Disposing_before_the_timer_fires_drops_the_unreported_text()
    {
        var harness = new TailHarness(capacity: 100, Interval);
        var decoder = harness.NewDecoder();
        harness.Append(decoder, "a");
        harness.Clock.Advance(10);
        harness.Append(decoder, "b");

        harness.Dispose();
        harness.Clock.Advance(500);
        harness.Timer.Fire();

        Assert.Equal(["a"], harness.Reports);
        Assert.True(harness.Timer.Disposed);
    }

    [Fact]
    public void Appending_after_dispose_is_a_no_op()
    {
        var harness = new TailHarness(capacity: 100, TimeSpan.Zero);
        var decoder = harness.NewDecoder();
        harness.Append(decoder, "a");
        harness.Dispose();

        harness.Append(decoder, "b");
        harness.Clock.Advance(1000);
        harness.Timer.Fire();

        Assert.Equal(["a"], harness.Reports);
    }

    [Fact]
    public void Disposing_twice_does_not_throw()
    {
        var harness = new TailHarness(capacity: 100, Interval);

        harness.Dispose();
        harness.Dispose();
    }

    [Fact]
    public void A_sink_that_throws_neither_propagates_nor_stops_later_reports()
    {
        var calls = 0;
        var harness = new TailHarness(capacity: 100, TimeSpan.Zero, onReport: _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("listener failed");
            }
        });
        var decoder = harness.NewDecoder();

        harness.Append(decoder, "a");
        harness.Append(decoder, "b");

        Assert.Equal(["a", "ab"], harness.Reports);
    }

    [Fact]
    public void A_sink_that_throws_from_the_timer_does_not_crash_the_timer_callback()
    {
        var harness = new TailHarness(capacity: 100, Interval, onReport: static text =>
        {
            if (text.Length > 1)
            {
                throw new InvalidOperationException("listener failed");
            }
        });
        var decoder = harness.NewDecoder();
        harness.Append(decoder, "a");
        harness.Clock.Advance(10);
        harness.Append(decoder, "b");
        harness.Clock.Advance(240);

        harness.Timer.Fire();

        Assert.Equal(["a", "ab"], harness.Reports);

        harness.Clock.Advance(10);
        harness.Append(decoder, "c");
        harness.Clock.Advance(240);
        harness.Timer.Fire();

        Assert.Equal(["a", "ab", "abc"], harness.Reports);
    }

    [Fact]
    public void An_interval_of_zero_reports_every_chunk()
    {
        var harness = new TailHarness(capacity: 100, TimeSpan.Zero);
        var decoder = harness.NewDecoder();

        for (var index = 0; index < 5; index++)
        {
            harness.Append(decoder, "x");
        }

        Assert.Equal(5, harness.Reports.Count);
        Assert.Equal("xxxxx", harness.Reports[^1]);
        Assert.Empty(harness.Timer.Changes);
    }

    [Fact]
    public void A_negative_interval_behaves_like_zero()
    {
        var harness = new TailHarness(capacity: 100, TimeSpan.FromSeconds(-5));
        var decoder = harness.NewDecoder();

        harness.Append(decoder, "a");
        harness.Append(decoder, "b");

        Assert.Equal(["a", "ab"], harness.Reports);
    }

    [Fact]
    public void A_null_sink_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new ProcessOutputTail(null!, 10, Interval, TimeProvider.System));
    }

    [Fact]
    public void Concurrent_appends_from_several_threads_never_throw_and_stay_within_the_capacity()
    {
        const int threads = 8;
        const int appends = 250;
        const int capacity = 64;
        var lengths = new List<int>();
        var gate = new object();
        var harness = new TailHarness(capacity, TimeSpan.Zero, onReport: text =>
        {
            lock (gate)
            {
                lengths.Add(text.Length);
            }
        });
        var failures = new List<Exception>();

        var workers = Enumerable.Range(0, threads).Select(worker => new Thread(() =>
        {
            try
            {
                var decoder = harness.NewDecoder();
                var bytes = Utf8(new string((char)('a' + worker), 7));
                for (var index = 0; index < appends; index++)
                {
                    harness.Append(decoder, bytes);
                }
            }
            catch (Exception exception)
            {
                lock (failures)
                {
                    failures.Add(exception);
                }
            }
        })).ToArray();

        foreach (var worker in workers)
        {
            worker.Start();
        }

        foreach (var worker in workers)
        {
            Assert.True(worker.Join(TestSupport.TestLimit));
        }

        Assert.Empty(failures);
        Assert.Equal(threads * appends, lengths.Count);
        Assert.All(lengths, length => Assert.InRange(length, 1, capacity));
        Assert.Equal(capacity, lengths.Max());
        Assert.All(harness.Reports, text => Assert.True(text.Length <= capacity));
    }

    [Fact]
    public void Concurrent_appends_and_timer_fires_never_lose_the_final_text_or_throw()
    {
        var harness = new TailHarness(capacity: 1000, Interval);
        var decoder = harness.NewDecoder();
        harness.Append(decoder, "first");
        harness.Clock.Advance(10);
        var failures = new List<Exception>();

        var appender = new Thread(() =>
        {
            try
            {
                for (var index = 0; index < 500; index++)
                {
                    harness.Append(decoder, "z");
                }
            }
            catch (Exception exception)
            {
                lock (failures)
                {
                    failures.Add(exception);
                }
            }
        });
        var firer = new Thread(() =>
        {
            try
            {
                for (var index = 0; index < 500; index++)
                {
                    harness.Clock.Advance(1);
                    harness.Timer.Fire();
                }
            }
            catch (Exception exception)
            {
                lock (failures)
                {
                    failures.Add(exception);
                }
            }
        });

        appender.Start();
        firer.Start();
        Assert.True(appender.Join(TestSupport.TestLimit));
        Assert.True(firer.Join(TestSupport.TestLimit));
        harness.Clock.Advance(1000);
        harness.Timer.Fire();

        Assert.Empty(failures);
        // Reports are delivered outside the lock, so two threads may deliver out of order; the complete text must be among them.
        Assert.Contains("first" + new string('z', 500), harness.Reports);
    }

    [Fact]
    public void Snapshots_reach_the_sink_in_order_and_never_concurrently()
    {
        // Capacity exceeds the total output, so nothing is ever trimmed and every snapshot must extend the previous one.
        const int appenders = 6;
        const int appends = 300;
        var inFlight = 0;
        var overlaps = 0;
        var lengths = new List<int>();
        var harness = new TailHarness(capacity: 100_000, TimeSpan.FromMilliseconds(5), onReport: text =>
        {
            if (Interlocked.Increment(ref inFlight) != 1)
            {
                Interlocked.Increment(ref overlaps);
            }

            // Written without any lock of its own: the tail's own serialisation is what is under test.
            lengths.Add(text.Length);
            Thread.SpinWait(50);
            Interlocked.Decrement(ref inFlight);
        });
        var failures = new List<Exception>();
        var done = 0;

        void Guard(Action body)
        {
            try
            {
                body();
            }
            catch (Exception exception)
            {
                lock (failures)
                {
                    failures.Add(exception);
                }
            }
        }

        var threads = Enumerable.Range(0, appenders).Select(worker => new Thread(() => Guard(() =>
        {
            var decoder = harness.NewDecoder();
            for (var index = 0; index < appends; index++)
            {
                harness.Append(decoder, "ab");
                if (index % 7 == 0)
                {
                    harness.Clock.Advance(1);
                }
            }

            Interlocked.Increment(ref done);
        }))).ToList();
        threads.Add(new Thread(() => Guard(() =>
        {
            while (Volatile.Read(ref done) < appenders)
            {
                harness.Clock.Advance(2);
                harness.Timer.Fire();
            }
        })));

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TestSupport.TestLimit));
        }

        harness.Clock.Advance(1000);
        harness.Timer.Fire();

        Assert.Empty(failures);
        Assert.Equal(0, Volatile.Read(ref overlaps));
        Assert.True(lengths.Count > 1);
        for (var index = 1; index < lengths.Count; index++)
        {
            Assert.True(lengths[index] >= lengths[index - 1], $"snapshot {index} shrank from {lengths[index - 1]} to {lengths[index]}");
        }

        Assert.Equal(appenders * appends * 2, lengths[^1]);
    }

    /// <summary>A clock that moves only when told to, at one tick per millisecond.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long now = 1_000_000;

        public ManualTimeProvider(ManualTimer timer) => Timer = timer;

        public ManualTimer Timer { get; }

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref now);

        public void Advance(long milliseconds) => Interlocked.Add(ref now, milliseconds);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer.Bind(callback, state);
            return Timer;
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly object gate = new();
        private readonly List<(TimeSpan Due, TimeSpan Period)> changes = [];
        private TimerCallback? callback;
        private object? state;

        public bool Disposed { get; private set; }

        /// <summary>Every <see cref="Change"/> that armed the timer (a finite due time).</summary>
        public IReadOnlyList<(TimeSpan Due, TimeSpan Period)> Changes
        {
            get
            {
                lock (gate)
                {
                    return [.. changes];
                }
            }
        }

        public void Bind(TimerCallback bound, object? boundState)
        {
            callback = bound;
            state = boundState;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                lock (gate)
                {
                    changes.Add((dueTime, period));
                }
            }

            return true;
        }

        public void Fire() => callback!(state);

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>One <see cref="ProcessOutputTail"/> over a manual clock and timer, with every report recorded.</summary>
    private sealed class TailHarness : IDisposable
    {
        private readonly object gate = new();
        private readonly List<string> reports = [];
        private readonly ProcessOutputTail tail;

        public TailHarness(int capacity, TimeSpan interval, Action<string>? onReport = null)
        {
            Timer = new ManualTimer();
            Clock = new ManualTimeProvider(Timer);
            tail = new ProcessOutputTail(
                text =>
                {
                    lock (gate)
                    {
                        reports.Add(text);
                    }

                    onReport?.Invoke(text);
                },
                capacity,
                interval,
                Clock);
        }

        public ManualTimer Timer { get; }

        public ManualTimeProvider Clock { get; }

        public IReadOnlyList<string> Reports
        {
            get
            {
                lock (gate)
                {
                    return [.. reports];
                }
            }
        }

        public Decoder NewDecoder() => Encoding.UTF8.GetDecoder();

        public void Append(Decoder decoder, string text) => tail.Append(decoder, Utf8(text));

        public void Append(Decoder decoder, ReadOnlySpan<byte> bytes) => tail.Append(decoder, bytes);

        public void Dispose() => tail.Dispose();
    }
}
