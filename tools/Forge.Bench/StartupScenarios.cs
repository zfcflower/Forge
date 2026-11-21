using System.Diagnostics;
using System.Globalization;
using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.Core.Storage;

namespace Forge.Bench;

/// <summary>
/// The <c>startup</c> command: what replaying one remembered session's history costs the UI thread. Restoring a tab reads every
/// stored message of the session (off the dispatcher) and then maps all of them into transcript rounds on it - which, times
/// every remembered tab, used to be the bulk of the time between the window appearing and the app being usable.
/// <para>A restored tab now defers this to its first activation, so the number is what that first click pays and what the
/// start-up gave up per deferred tab.</para>
/// </summary>
internal static class StartupScenarios
{
    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Replay(fixtures, 200);
        yield return Replay(fixtures, 2_000);
    }

    /// <summary>A conversation of <paramref name="turns"/> question/answer pairs, replayed into a fresh transcript per call.</summary>
    private static BenchScenario Replay(Fixtures fixtures, int turns)
    {
        return new BenchScenario(
            $"startup.replay.{turns}turns",
            "startup",
            Heavy: turns >= 2_000,
            PrepareAsync: async cancellationToken =>
            {
                var directory = fixtures.Workspace.DirectoryAt($"startup/replay-{turns}");
                var store = new SqliteSessionStore(directory);
                var session = await store.CreateAsync(new SessionCreate(directory, $"replay-{turns}"), cancellationToken)
                    .ConfigureAwait(false);
                var question = new string('q', 400);
                var answer = new string('a', 1_200);
                for (var turn = 0; turn < turns; turn++)
                {
                    await store.AppendAsync(session.Id, ChatMessage.User(question), null, cancellationToken).ConfigureAwait(false);
                    await store.AppendAsync(
                            session.Id,
                            ChatMessage.Assistant(answer),
                            new TokenUsage(100, 50),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                var sessionId = session.Id;
                return new BenchCase
                {
                    Warmup = 1,
                    Iterations = 5,
                    ProcessWideAllocation = true,
                    Note = $"{turns} turns, read off-thread, mapped on the caller's thread",
                    Run = async cancellationToken =>
                    {
                        var watch = Stopwatch.StartNew();
                        var messages = await TranscriptReplay.ReadAsync(store, sessionId, cancellationToken).ConfigureAwait(false);
                        var readMs = watch.Elapsed.TotalMilliseconds;
                        watch.Restart();
                        var transcript = new TranscriptViewModel(collapseOlderTurns: false);
                        TranscriptReplay.Apply(transcript, messages);
                        var applyMs = watch.Elapsed.TotalMilliseconds;
                        Console.Error.WriteLine(string.Create(
                            CultureInfo.InvariantCulture,
                            $"startup.replay.{turns}turns: read {readMs:F1} ms · apply {applyMs:F1} ms · {messages.Count} messages, {transcript.Rounds.Count} rounds"));
                        return new OpResult(
                            Bytes: (long)(readMs + applyMs),
                            Items: messages.Count,
                            Signature: transcript.Rounds.Count,
                            Observation: $"read {readMs:F0} + apply {applyMs:F0} ms, {transcript.Rounds.Count} rounds");
                    },
                };
            });
    }
}
