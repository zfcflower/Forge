using System.Globalization;
using System.Reflection;
using Forge.Plugin.Sdk;

namespace Forge.Scripting;

/// <summary>
/// The entry point a compiled script image exposes: <c>Task&lt;object?&gt; (ScriptGlobals, CancellationToken)</c>. The
/// compiler emits exactly this signature and the descriptor names its type and method, so an executor can bind it
/// without knowing anything about Roslyn.
/// </summary>
public delegate Task<object?> ScriptEntryPoint(ScriptGlobals globals, CancellationToken ct);

/// <summary>Everything one run needs that is not code: the tool metadata of this run, its bounds and its budget.</summary>
public sealed record ScriptRunSettings(
    IReadOnlyList<ToolDeclaration> Tools,
    CodeExecutionOptions Options,
    int TimeoutMilliseconds);

/// <summary>
/// The run loop of one script, host-free: it builds this run's facade, globals and token, starts the entry point off the
/// calling thread, waits <em>under</em> the budget and turns the outcome into a <see cref="CodeExecutionResult"/>. It
/// resolves no tool, evaluates no approval, and loads nothing; the caller supplies the entry point and the facade.
/// <para>
/// Cancellation is cooperative for the script but never blocking for the caller: the budget is enforced by waiting on
/// the script, so a synchronous infinite loop cannot hold the caller. A script still running after the budget or the
/// caller's token ends the wait is <em>abandoned</em>, not stopped, and the result has
/// <see cref="CodeExecutionResult.RequiresTermination"/> set: the executor process must be killed, and it is never
/// reused. An abandoned run keeps its thread and its token source alive on purpose, so its next callback sees the
/// cancellation instead of a disposed object.
/// </para>
/// </summary>
public static class ScriptHarness
{
    /// <summary>
    /// How long a script gets to unwind once cancellation has ended the wait. Inside this window it stopped on its own;
    /// outside it, it was abandoned and the executor must be terminated.
    /// </summary>
    public static readonly TimeSpan AbandonmentGrace = TimeSpan.FromMilliseconds(1000);

    private static readonly Dictionary<Type, PropertyInfo> ResultProperties = [];

    /// <summary>Validates the settings a caller hands to one run; an invalid configuration throws.</summary>
    public static ScriptRunSettings Validate(ScriptRunSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = settings.Options ??
            throw new ArgumentNullException(nameof(settings), "The run carries no execution options.");
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentToolCalls, 1, nameof(options.MaxConcurrentToolCalls));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxScriptChars, 1, nameof(options.MaxScriptChars));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxResultChars, 1, nameof(options.MaxResultChars));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxInnerResultChars, 1, nameof(options.MaxInnerResultChars));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxLogChars, 1, nameof(options.MaxLogChars));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDiagnosticChars, 1, nameof(options.MaxDiagnosticChars));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDiagnosticCount, 1, nameof(options.MaxDiagnosticCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CompilationCacheCapacity, 0, nameof(options.CompilationCacheCapacity));
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.TimeoutMilliseconds, 1, "settings.TimeoutMilliseconds");
        ValidateTools(settings.Tools);
        return settings;
    }

    /// <summary>Validates tool metadata: the list is required, entries are non-null, names are non-blank and the side
    /// effect is a defined value. An empty list is allowed for a pure-computation script.</summary>
    public static void ValidateTools(IReadOnlyList<ToolDeclaration>? tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        foreach (var tool in tools)
        {
            if (tool is null)
            {
                throw new ArgumentException("Tool metadata entries must not be null.", nameof(tools));
            }

            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                throw new ArgumentException("Tool names must not be blank.", nameof(tools));
            }

            if (tool.SideEffect is not (ToolSideEffect.ReadOnly or ToolSideEffect.Write or ToolSideEffect.Execute))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tools),
                    tool.SideEffect,
                    $"Tool '{tool.Name}' carries an undefined side effect.");
            }
        }
    }

    /// <summary>
    /// Runs the already-loaded entry point once. Cancellation requested before the run throws
    /// <see cref="OperationCanceledException"/> without starting any script code; cancellation during the run returns a
    /// normal error result.
    /// </summary>
    public static async Task<CodeExecutionResult> RunAsync(
        ScriptEntryPoint entry,
        IHostFacade host,
        ScriptRunSettings settings,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(host);
        Validate(settings);

        var options = settings.Options;
        ct.ThrowIfCancellationRequested();

        var log = new ScriptLog(options.MaxLogChars);
        var timeout = TimeSpan.FromMilliseconds(settings.TimeoutMilliseconds);
        var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var runToken = runCts.Token;
        ScriptRunOutcome outcome;
        try
        {
            runCts.CancelAfter(timeout);
            var facade = new ToolsFacade(
                host,
                settings.Tools,
                options.MaxConcurrentToolCalls,
                options.MaxInnerResultChars,
                runToken);
            var globals = new ScriptGlobals(facade, log.Write, runToken);
            outcome = await StartAsync(entry, globals, runToken).ConfigureAwait(false);
        }
        catch
        {
            runCts.Dispose();
            throw;
        }

        if (outcome.Abandoned)
        {
            // The abandoned script still holds the token; disposing the source would make its next callback fail with
            // ObjectDisposedException instead of the cancellation it should see. The host kills the process anyway.
            return new CodeExecutionResult(
                AppendLog(AbandonedMessage(ct.IsCancellationRequested, timeout), log),
                IsError: true,
                RequiresTermination: true);
        }

        runCts.Dispose();

        if (outcome.Error is null)
        {
            var text = TextBounds.Truncate(ScriptOutput.Format(outcome.Value), options.MaxResultChars, "result");
            return new CodeExecutionResult(AppendLog(text, log));
        }

        var stopped = runToken.IsCancellationRequested
            ? StoppedMessage(ct.IsCancellationRequested, timeout)
            : "The script threw " + ScriptOutput.Describe(outcome.Error);
        return new CodeExecutionResult(AppendLog(stopped, log), IsError: true);
    }

    /// <summary>
    /// Starts the script off the calling (IPC) thread and waits under the run token. Waiting - not the script - is what
    /// the budget ends, so a synchronous infinite loop cannot hold the caller; a script that does not stop within the
    /// grace window is left abandoned and observed so its late exception does not go unhandled.
    /// </summary>
    private static async Task<ScriptRunOutcome> StartAsync(
        ScriptEntryPoint entry,
        ScriptGlobals globals,
        CancellationToken runToken)
    {
        Func<Task<object?>> start = () => entry(globals, runToken);
        var execution = Task.Run(start, CancellationToken.None);

        var (outcome, error) = await WaitUnderBudgetAsync(execution, runToken).ConfigureAwait(false);
        if (outcome != WaitOutcome.Completed)
        {
            return new ScriptRunOutcome(null, error, Abandoned: outcome == WaitOutcome.Abandoned);
        }

        var value = await execution.ConfigureAwait(false);

        // A script whose last expression is a task: await it under the same budget, then take its result.
        if (value is Task pending)
        {
            (outcome, error) = await WaitUnderBudgetAsync(pending, runToken).ConfigureAwait(false);
            if (outcome != WaitOutcome.Completed)
            {
                return new ScriptRunOutcome(null, error, Abandoned: outcome == WaitOutcome.Abandoned);
            }

            value = ResultOf(pending);
        }

        return new ScriptRunOutcome(value, null, Abandoned: false);
    }

    /// <summary>
    /// Waits for <paramref name="task"/> under the run token. When the token ends the wait, the task is given a bounded
    /// grace window to unwind on its own: only a task that is still running when the window closes is abandoned, and an
    /// abandoned task is never described as stopped.
    /// </summary>
    private static async Task<(WaitOutcome Outcome, Exception? Error)> WaitUnderBudgetAsync(
        Task task,
        CancellationToken runToken)
    {
        try
        {
            await task.WaitAsync(runToken).ConfigureAwait(false);
            return (WaitOutcome.Completed, null);
        }
        catch (OperationCanceledException ex) when (runToken.IsCancellationRequested && !task.IsCompleted)
        {
            var stopped = await StopsWithinAsync(task, AbandonmentGrace).ConfigureAwait(false);
            Observe(task);
            return stopped ? (WaitOutcome.Stopped, ex) : (WaitOutcome.Abandoned, ex);
        }
        catch (Exception ex)
        {
            return (WaitOutcome.Stopped, ex);
        }
    }

    private static async Task<bool> StopsWithinAsync(Task task, TimeSpan grace)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        var settled = await Task.WhenAny(task, Task.Delay(grace)).ConfigureAwait(false);
        return ReferenceEquals(settled, task);
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            static abandoned => _ = abandoned.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static object? ResultOf(Task task)
    {
        var type = task.GetType();
        lock (ResultProperties)
        {
            if (ResultProperties.TryGetValue(type, out var cached))
            {
                return cached.GetValue(task);
            }
        }

        var property = type.GetProperty("Result");
        if (property is null)
        {
            return null;
        }

        lock (ResultProperties)
        {
            ResultProperties[type] = property;
        }

        return property.GetValue(task);
    }

    private static string AppendLog(string text, ScriptLog log)
    {
        var captured = log.Snapshot();
        return captured.Length == 0 ? text : text + "\n[log]\n" + captured;
    }

    private static string AbandonedMessage(bool cancelled, TimeSpan timeout) => cancelled
        ? "The run was cancelled while the script was still executing: it did not stop when cancellation was requested, so it was abandoned. The executor process must be terminated; the partial log below is all that was captured."
        : string.Create(
            CultureInfo.InvariantCulture,
            $"The script did not stop after the {Seconds(timeout)} s run_code timeout: it ignored cancellation and was abandoned. The executor process must be terminated; the partial log below is all that was captured. Narrow the work, raise the timeout, or split it into several calls.");

    private static string StoppedMessage(bool cancelled, TimeSpan timeout) => cancelled
        ? "The run was cancelled and the script stopped."
        : string.Create(
            CultureInfo.InvariantCulture,
            $"The script was stopped after the {Seconds(timeout)} s run_code timeout. Narrow the work, raise the timeout, or split it into several calls.");

    private static string Seconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private readonly record struct ScriptRunOutcome(object? Value, Exception? Error, bool Abandoned);

    private enum WaitOutcome
    {
        Completed = 0,
        Stopped = 1,
        Abandoned = 2,
    }
}
