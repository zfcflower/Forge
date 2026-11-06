using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.HostClient.Tests.Terminal;

/// <summary>A <c>terminal.start</c> cancelled by its caller before the answer arrives: the caller gets the cancellation at
/// once, the request stays alive, and a terminal the host still built is stopped and its early frames dropped. A refused or
/// lost start has nothing to stop.</summary>
public sealed class TerminalStartCancellationTests
{
    [Fact]
    public async Task Cancelled_start_throws_then_a_late_answer_makes_the_client_stop_that_terminal_and_drop_its_early_frames()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        const string terminalId = "t-cancelled";
        var releaseAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.On(
            TerminalOps.Start,
            async (_, ct) =>
            {
                await releaseAnswer.Task.WaitAsync(ct).ConfigureAwait(false);
                return Json.Element(TerminalFixtures.StartResultJson(terminalId));
            });

        using var cancellation = new CancellationTokenSource();
        var starting = client.StartAsync(TerminalFixtures.Request(), cancellation.Token).AsTask();
        await host.WaitForRequestsAsync(TerminalOps.Start, 1).Bounded();

        // The host already produced output for the terminal it is about to announce.
        await host.NotifyClientAsync(TerminalOps.Output, TerminalFixtures.OutputJson(terminalId, 0, "stale"));
        await host.SyncAsync();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.Bounded());

        // The answer has not arrived, so no terminal id is known and nothing may be stopped yet.
        Assert.Empty(host.Requests(TerminalOps.Stop));

        releaseAnswer.TrySetResult();
        var stops = await host.WaitForRequestsAsync(TerminalOps.Stop, 1).Bounded();
        var stop = Assert.Single(stops);
        Assert.Equal(terminalId, stop.Arguments!.Value.GetProperty("terminalId").GetString());

        // The stale frame was dropped: a later terminal that reuses the id sees only its own output.
        TerminalFixtures.AnswerStart(host, terminalId);
        using var limit = TestLimit.Create();
        var second = client.StartAsync(TerminalFixtures.Request(), limit.Token).AsTask();
        var session = await second.Bounded();
        await host.NotifyClientAsync(TerminalOps.Output, TerminalFixtures.OutputJson(terminalId, 0, "fresh"));
        await host.NotifyClientAsync(TerminalOps.Exit, TerminalFixtures.ExitJson(terminalId, 0));
        await host.SyncAsync();

        var body = await TerminalFixtures.DrainAsync(session);
        Assert.Equal("fresh", System.Text.Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task Already_cancelled_token_throws_without_sending_a_start()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.StartAsync(TerminalFixtures.Request(), cancelled.Token).AsTask());

        Assert.Empty(host.Requests(TerminalOps.Start));
        Assert.Empty(host.Requests(TerminalOps.Stop));
    }

    [Fact]
    public async Task Cancellation_that_races_with_the_answer_still_stops_the_terminal_the_host_built()
    {
        await using var host = new FakeHost();
        await using var client = ClientFactory.Create(host);
        await client.StartConnectedAsync(host);
        const string terminalId = "t-raced";
        using var cancellation = new CancellationTokenSource();

        // The host cancels the caller and answers in the same breath, so the answer may already be in when the
        // cancellation is observed. The race cannot be pinned to one order from outside, but either order must end with
        // the terminal stopped (before the fix, the answer-first order orphaned it).
        host.On(
            TerminalOps.Start,
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(Json.Element(TerminalFixtures.StartResultJson(terminalId)));
            });

        var starting = client.StartAsync(TerminalFixtures.Request(), cancellation.Token).AsTask();
        try
        {
            var session = await starting.Bounded();
            Assert.NotNull(session); // the answer won the race and was handed out normally
        }
        catch (OperationCanceledException)
        {
            var stops = await host.WaitForRequestsAsync(TerminalOps.Stop, 1).Bounded();
            Assert.Equal(terminalId, Assert.Single(stops).Arguments!.Value.GetProperty("terminalId").GetString());
        }
    }

    [Fact]
    public async Task Cancelled_start_that_the_host_then_refuses_sends_no_stop_and_leaves_no_unobserved_exception()
    {
        var unobserved = new List<Exception>();
        void Observe(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            lock (unobserved)
            {
                unobserved.Add(args.Exception);
            }
        }

        TaskScheduler.UnobservedTaskException += Observe;
        try
        {
            await using var host = new FakeHost();
            await using var client = ClientFactory.Create(host);
            await client.StartConnectedAsync(host);
            var releaseAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.On(
                TerminalOps.Start,
                async (_, ct) =>
                {
                    await releaseAnswer.Task.WaitAsync(ct).ConfigureAwait(false);
                    throw new FakeHostError("terminal_limit", "refused-after-cancel");
                });

            using var cancellation = new CancellationTokenSource();
            var starting = client.StartAsync(TerminalFixtures.Request(), cancellation.Token).AsTask();
            await host.WaitForRequestsAsync(TerminalOps.Start, 1).Bounded();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.Bounded());

            releaseAnswer.TrySetResult();
            // Round trip: the refusal frame reached the client (frames are handled in order) before the check below.
            await host.SyncAsync();

            Assert.Empty(host.Requests(TerminalOps.Stop));
            ForceFinalization();
            Assert.DoesNotContain(Snapshot(unobserved), exception => exception.ToString().Contains("refused-after-cancel", StringComparison.Ordinal));
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Observe;
        }
    }

    [Fact]
    public async Task Cancelled_start_whose_connection_then_drops_sends_no_stop_and_leaves_no_unobserved_exception()
    {
        var unobserved = new List<Exception>();
        void Observe(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            lock (unobserved)
            {
                unobserved.Add(args.Exception);
            }
        }

        TaskScheduler.UnobservedTaskException += Observe;
        try
        {
            await using var host = new FakeHost();
            await using var client = ClientFactory.Create(host);
            var statuses = new EventLog<(HostStatus Status, string? Detail)>();
            client.StatusChanged += (status, detail) => statuses.Add((status, detail));
            await client.StartConnectedAsync(host);
            host.On(TerminalOps.Start, FakeHost.NeverAnswer);

            using var cancellation = new CancellationTokenSource();
            var starting = client.StartAsync(TerminalFixtures.Request(), cancellation.Token).AsTask();
            await host.WaitForRequestsAsync(TerminalOps.Start, 1).Bounded();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.Bounded());

            await host.DropConnectionAsync();
            await statuses.UntilAsync(events => events.Any(e => e.Status == HostStatus.Failed));

            Assert.Empty(host.Requests(TerminalOps.Stop));
            ForceFinalization();
            Assert.DoesNotContain(
                Snapshot(unobserved),
                exception => exception is IpcConnectionClosedException || exception.InnerException is IpcConnectionClosedException);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Observe;
        }
    }

    private static Exception[] Snapshot(List<Exception> list)
    {
        lock (list)
        {
            return [.. list.SelectMany(exception => exception is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [exception])];
        }
    }

    private static void ForceFinalization()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
