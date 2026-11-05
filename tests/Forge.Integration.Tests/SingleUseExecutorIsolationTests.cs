using System.Collections.Concurrent;
using Forge.Plugin.Sdk;
using Forge.ScriptRuntime;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Isolation tests for the single-use executor: a discarded call must leave nothing behind, and two calls must
/// share nothing. Every script is compiler-built and every run goes over the wire.</summary>
public sealed class SingleUseExecutorIsolationTests
{
    private static readonly TimeSpan StepLimit = TestBudgets.Safety(TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(10));
    private static readonly CodeExecutionOptions Options = new();
    private const int RunTimeoutMilliseconds = 15000;

    [Fact]
    public async Task GhostTask_DiesWithTheProcessTree_AndNeverWritesItsMarker()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var marker = Path.Combine(Path.GetTempPath(), "forge-exec-ghost-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var compiled = await SingleUseExecutorArtifacts.CompileValidAsync(
                "async Task Ghost()\n{\n    await Task.Delay(2500);\n    await Tools.Call(\"writeMarker\", new {});\n}\n_ = Ghost();\nreturn \"started\";",
                Options,
                ct);
            var tools = new[] { new ToolDeclaration("writeMarker", "Writes the marker.", "{\"type\":\"object\"}", ToolSideEffect.Write) };
            await using var session = await SingleUseExecutorSession.StartAsync(ct);
            session.ToolResponder = _ =>
            {
                File.WriteAllText(marker, "ghost-wrote-this");
                return new ToolResultPayload("written");
            };
            await SingleUseExecutorArtifacts.LoadAsync(session, compiled, ct);
            var result = await SingleUseExecutorArtifacts.RunOnceAsync(
                session,
                SingleUseExecutorArtifacts.RunRequest(SingleUseExecutorArtifacts.NewRunId(), tools, Options, RunTimeoutMilliseconds),
                ct);
            Assert.False(result.IsError, result.Content);
            Assert.Equal("started", result.Content);

            var pid = session.ProcessId;
            Assert.True(pid > 0, "The executor has no process id.");
            session.KillTree();
            using (var exited = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                try
                {
                    await session.WaitForExitAsync(exited.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"The executor process tree (pid {pid}) was still alive 10 s after the kill.");
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(4000), ct);
            Assert.False(File.Exists(marker), "A background task outlived the killed executor and wrote its marker.");
            Assert.False(SingleUseExecutorSession.IsRunning(pid), $"The executor process tree (pid {pid}) survived the kill.");
        }
        finally
        {
            try
            {
                if (File.Exists(marker))
                {
                    File.Delete(marker);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task StaticState_DoesNotLeakAcrossTwoSeparateExecutors()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        const string code =
            "var before = CultureInfo.DefaultThreadCurrentCulture?.Name ?? \"none\";\n" +
            "CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;\n" +
            "return before;";
        await using var first = await SingleUseExecutorSession.StartAsync(ct);
        await using var second = await SingleUseExecutorSession.StartAsync(ct);
        try
        {
            var compiled = await SingleUseExecutorArtifacts.CompileValidAsync(code, Options, ct);
            await SingleUseExecutorArtifacts.LoadAsync(first, compiled, ct);
            await SingleUseExecutorArtifacts.LoadAsync(second, compiled, ct);
            var firstResult = await SingleUseExecutorArtifacts.RunOnceAsync(
                first,
                SingleUseExecutorArtifacts.RunRequest(SingleUseExecutorArtifacts.NewRunId(), [], Options, RunTimeoutMilliseconds),
                ct);
            var secondResult = await SingleUseExecutorArtifacts.RunOnceAsync(
                second,
                SingleUseExecutorArtifacts.RunRequest(SingleUseExecutorArtifacts.NewRunId(), [], Options, RunTimeoutMilliseconds),
                ct);
            Assert.False(firstResult.IsError, firstResult.Content);
            Assert.False(secondResult.IsError, secondResult.Content);
            Assert.Equal("none", firstResult.Content);
            Assert.Equal("none", secondResult.Content);
            Assert.True(first.ProcessId > 0 && second.ProcessId > 0, "An executor has no process id.");
            Assert.NotEqual(first.ProcessId, second.ProcessId);
        }
        finally
        {
            first.KillTree();
            second.KillTree();
        }
    }

    [Fact]
    public async Task ConcurrentSessions_EachReturnTheirOwnResult_WithDistinctProcesses_AndLeaveNothingBehind()
    {
        const int count = 4;
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var sessions = new ConcurrentBag<SingleUseExecutorSession>();
        var pids = new ConcurrentBag<int>();
        try
        {
            var outcomes = await Task.WhenAll(Enumerable.Range(0, count).Select(async slot =>
            {
                var compiled = await SingleUseExecutorArtifacts.CompileValidAsync($"return \"slot-{slot}\";", Options, ct);
                var session = await SingleUseExecutorSession.StartAsync(ct);
                sessions.Add(session);
                await SingleUseExecutorArtifacts.LoadAsync(session, compiled, ct);
                var result = await SingleUseExecutorArtifacts.RunOnceAsync(
                    session,
                    SingleUseExecutorArtifacts.RunRequest(SingleUseExecutorArtifacts.NewRunId(), [], Options, RunTimeoutMilliseconds),
                    ct);
                pids.Add(session.ProcessId);
                return (Slot: slot, Content: result.Content, IsError: result.IsError);
            }));
            Assert.Equal(count, outcomes.Length);
            foreach (var outcome in outcomes)
            {
                Assert.False(outcome.IsError, outcome.Content);
                Assert.Equal($"slot-{outcome.Slot}", outcome.Content);
            }

            var ids = pids.ToArray();
            Assert.Equal(count, ids.Length);
            Assert.All(ids, pid => Assert.True(pid > 0, $"An executor has no process id (pid {pid})."));
            Assert.Equal(count, ids.Distinct().Count());
        }
        finally
        {
            foreach (var session in sessions)
            {
                session.KillTree();
            }

            foreach (var session in sessions)
            {
                using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await session.WaitForExitAsync(exited.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException("An executor process tree was still alive 10 s after the kill.");
                }

                await session.DisposeAsync();
            }
        }

        foreach (var pid in pids)
        {
            Assert.False(SingleUseExecutorSession.IsRunning(pid), $"An executor process (pid {pid}) survived its kill.");
        }
    }

}
