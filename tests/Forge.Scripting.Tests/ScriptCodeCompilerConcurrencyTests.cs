using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Scripting.Tests;

/// <summary>Single-flight, per-caller cancellation and cache-isolation behavior of
/// <see cref="ScriptCodeCompiler.CompileAsync"/>.</summary>
public sealed class ScriptCodeCompilerConcurrencyTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Concurrent_calls_for_one_key_share_one_compile()
    {
        var compiler = new ScriptCodeCompiler();
        var options = new CodeExecutionOptions();
        const string Code = "return 42;";

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => compiler.CompileAsync(Code, options, CancellationToken.None)));

        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.All(results, result => Assert.Same(results[0].Image, result.Image));
        Assert.True(results[0].Ok);
    }

    // The tests below inject the internal compile delegate, so they only compile where InternalsVisibleTo exists.
#if DEBUG
    [Fact]
    public async Task Cancelled_waiter_leaves_other_waiters_unaffected()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compiler = Compiler(ct =>
        {
            entered.TrySetResult();
            release.Task.Wait(ct);
            ct.ThrowIfCancellationRequested();
            return CompiledScript.Success(Success());
        });
        using var ctsA = new CancellationTokenSource();
        var options = new CodeExecutionOptions();
        const string Code = "return 1;";

        var taskA = compiler.CompileAsync(Code, options, ctsA.Token);
        await entered.Task.WaitAsync(Budget);
        var taskB = compiler.CompileAsync(Code, options, CancellationToken.None);

        ctsA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);

        release.TrySetResult();
        var resultB = await taskB.WaitAsync(Budget);

        Assert.True(resultB.Ok);
    }
#endif

#if DEBUG
    [Fact]
    public async Task All_waiters_cancelled_cancels_shared_compile_and_next_call_recompiles()
    {
        using var cancelled = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compileCount = 0;
        var compiler = Compiler(ct =>
        {
            Interlocked.Increment(ref compileCount);
            entered.TrySetResult();
            using var registration = ct.Register(static state => ((ManualResetEventSlim)state!).Set(), cancelled);
            cancelled.Wait();
            ct.ThrowIfCancellationRequested();
            return CompiledScript.Success(Success());
        });
        using var ctsA = new CancellationTokenSource();
        using var ctsB = new CancellationTokenSource();
        var options = new CodeExecutionOptions();
        const string Code = "return 1;";

        var taskA = compiler.CompileAsync(Code, options, ctsA.Token);
        await entered.Task.WaitAsync(Budget);
        var taskB = compiler.CompileAsync(Code, options, ctsB.Token);

        ctsA.Cancel();
        ctsB.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskB);

        var next = await compiler.CompileAsync(Code, options, CancellationToken.None).WaitAsync(Budget);

        Assert.True(next.Ok);
        Assert.Equal(2, Volatile.Read(ref compileCount));
    }
#endif

#if DEBUG
    [Fact]
    public async Task Cancellation_racing_completion_returns_success_or_clean_cancellation_only()
    {
        for (var iteration = 0; iteration < 40; iteration++)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var compiler = Compiler(ct =>
            {
                entered.TrySetResult();
                release.Task.Wait(ct);
                ct.ThrowIfCancellationRequested();
                return CompiledScript.Success(Success());
            });
            using var ctsA = new CancellationTokenSource();
            using var ctsB = new CancellationTokenSource();
            var options = new CodeExecutionOptions();
            const string Code = "return 1;";

            var taskA = compiler.CompileAsync(Code, options, ctsA.Token);
            await entered.Task.WaitAsync(Budget);
            var taskB = compiler.CompileAsync(Code, options, ctsB.Token);

            var releaseTask = Task.Run(release.SetResult);
            var cancelTask = Task.Run(() =>
            {
                ctsA.Cancel();
                ctsB.Cancel();
            });
            await Task.WhenAll(releaseTask, cancelTask);

            ScriptCompilation? shared = null;
            foreach (var task in new[] { taskA, taskB })
            {
                try
                {
                    var result = await task.WaitAsync(Budget);
                    Assert.True(result.Ok);
                    if (shared is null)
                    {
                        shared = result;
                    }
                    else
                    {
                        Assert.Same(shared, result);
                    }
                }
                catch (OperationCanceledException)
                {
                    // The caller cancelled at the same moment the compile finished; a clean cancellation is allowed.
                }
            }

            var next = await compiler.CompileAsync(Code, options, CancellationToken.None).WaitAsync(Budget);
            Assert.True(next.Ok);
        }
    }
#endif

    [Fact]
    public async Task Many_waiters_with_random_cancellation_do_not_deadlock_and_share_one_result()
    {
        const int Callers = 64;
        var random = new Random(1234);
        var shouldCancel = new bool[Callers];
        for (var i = 0; i < Callers; i++)
        {
            shouldCancel[i] = random.Next(2) == 0;
        }

        shouldCancel[0] = false;
        shouldCancel[1] = true;

        var compiler = new ScriptCodeCompiler();
        var options = new CodeExecutionOptions();
        const string Code = "return 42;";
        var tasks = new Task<ScriptCompilation>[Callers];
        var sources = new CancellationTokenSource[Callers];

        for (var i = 0; i < Callers; i++)
        {
            var cts = new CancellationTokenSource();
            sources[i] = cts;
            if (shouldCancel[i])
            {
                cts.Cancel();
            }

            tasks[i] = compiler.CompileAsync(Code, options, cts.Token);
        }

        ScriptCompilation? shared = null;
        for (var i = 0; i < Callers; i++)
        {
            try
            {
                var result = await tasks[i].WaitAsync(Budget);
                Assert.True(result.Ok);
                if (shared is null)
                {
                    shared = result;
                }
                else
                {
                    Assert.Same(shared, result);
                    Assert.Same(shared.Image, result.Image);
                }
            }
            catch (OperationCanceledException)
            {
                Assert.True(shouldCancel[i]);
            }
        }

        Assert.NotNull(shared);

        foreach (var source in sources)
        {
            source.Dispose();
        }
    }

#if DEBUG
    private static ScriptCodeCompiler Compiler(Func<CancellationToken, CompiledScript> compile) =>
        new((_, _, _, ct) => compile(ct));
#endif

    private static ScriptCompilation Success() =>
        new(true, "key", new string('a', 64), [1], null, null);
}
