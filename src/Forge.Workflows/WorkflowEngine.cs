using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Forge.Plugin.Sdk;

namespace Forge.Workflows;

/// <summary>
/// Executes a <see cref="WorkflowDefinition"/>: steps run as soon as their <c>after</c> dependencies have
/// completed, at most <see cref="RunOptions.MaxConcurrency"/> at a time, in stable definition order.
/// Tool calls go through <see cref="IHostFacade.CallToolAsync"/>; failures are recorded in the result, never thrown.
/// </summary>
public sealed class WorkflowEngine
{
    private const int Wake = -1;

    private static readonly JsonDocument EmptyArguments = JsonDocument.Parse("{}");

    private static readonly JsonWriterOptions ArgumentWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true,
        Indented = false,
    };

    /// <summary>Validates and runs <paramref name="definition"/>.</summary>
    /// <param name="definition">The workflow to run.</param>
    /// <param name="host">Facade used for every tool call.</param>
    /// <param name="options">Concurrency, timeout and clock.</param>
    /// <param name="ct">Stops scheduling new steps; already running steps receive it as well.</param>
    public async Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition definition,
        IHostFacade host,
        RunOptions options,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxConcurrency, "MaxConcurrency must be at least 1.");
        }

        if (options.Timeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), timeout, "Timeout must be positive when it is set.");
        }

        if (options.CancellationDrainTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.CancellationDrainTimeout,
                "CancellationDrainTimeout must not be negative.");
        }

        var diagnostics = new List<WorkflowDiagnostic>();
        var plan = WorkflowPlanBuilder.Build(definition, diagnostics);
        if (plan is null)
        {
            return InvalidResult(definition, diagnostics, options.TimeProvider);
        }

        return await new Run(plan, host, options).ExecuteAsync(ct).ConfigureAwait(false);
    }

    private static WorkflowRunResult InvalidResult(
        WorkflowDefinition definition,
        List<WorkflowDiagnostic> diagnostics,
        TimeProvider clock)
    {
        var steps = new StepResult[definition.Steps.Count];
        var neverRan = new string[steps.Length];
        for (var i = 0; i < steps.Length; i++)
        {
            var step = definition.Steps[i];
            var stepId = step?.Id;
            var id = string.IsNullOrWhiteSpace(stepId) ? $"#{i}" : stepId;
            steps[i] = new StepResult
            {
                Index = i,
                Id = id,
                Tool = step?.Tool ?? string.Empty,
                Status = StepStatus.NotRun,
                Error = "the workflow definition is invalid",
                DependsOn = step?.After ?? [],
            };
            neverRan[i] = id;
        }

        string? error = null;
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Severity == WorkflowDiagnosticSeverity.Error)
            {
                error = diagnostic.Message;
                break;
            }
        }

        var now = clock.GetUtcNow();
        return new WorkflowRunResult
        {
            Status = WorkflowRunStatus.Invalid,
            Name = definition.Name,
            Steps = steps,
            Diagnostics = diagnostics,
            Error = error is null ? "the workflow definition is invalid" : $"the workflow definition is invalid: {error}",
            StartedAt = now,
            CompletedAt = now,
            Duration = TimeSpan.Zero,
            NeverRanStepIds = neverRan,
        };
    }

    /// <summary>State of one execution. A run is not reusable.</summary>
    private sealed class Run(WorkflowPlan plan, IHostFacade host, RunOptions options)
    {
        private readonly WorkflowPlan _plan = plan;
        private readonly IHostFacade _host = host;
        private readonly RunOptions _options = options;
        private readonly TimeProvider _clock = options.TimeProvider;
        private readonly RunState _state = new(plan);
        private readonly DateTimeOffset[] _startedAt = new DateTimeOffset[plan.Steps.Length];
        private readonly long[] _startedTimestamp = new long[plan.Steps.Length];
        private readonly DateTimeOffset[] _completedAt = new DateTimeOffset[plan.Steps.Length];
        private readonly TimeSpan[] _duration = new TimeSpan[plan.Steps.Length];
        private readonly Channel<int> _completions = Channel.CreateUnbounded<int>(
            new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

        private readonly Dictionary<int, Task> _running = [];
        private CancellationToken _runToken;

        public async Task<WorkflowRunResult> ExecuteAsync(CancellationToken ct)
        {
            var steps = _plan.Steps;
            var count = steps.Length;
            var remaining = new int[count];
            var ready = new SortedSet<int>();
            for (var i = 0; i < count; i++)
            {
                remaining[i] = steps[i].DependencyIndices.Length;
                if (remaining[i] == 0)
                {
                    ready.Add(i);
                }
            }

            var startedAt = _clock.GetUtcNow();
            var startedTimestamp = _clock.GetTimestamp();
            var inFlight = new HashSet<int>();

            using var timeoutCts = _options.Timeout is { } timeout ? new CancellationTokenSource(timeout, _clock) : null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts?.Token ?? CancellationToken.None);
            _runToken = linked.Token;
            using var wake = linked.Token.Register(
                static writer => ((ChannelWriter<int>)writer!).TryWrite(Wake),
                _completions.Writer);

            CancellationTokenSource? drain = null;
            try
            {
                while (true)
                {
                    while (!linked.IsCancellationRequested && inFlight.Count < _options.MaxConcurrency && ready.Count > 0)
                    {
                        var next = ready.Min;
                        ready.Remove(next);
                        inFlight.Add(next);
                        _startedAt[next] = _clock.GetUtcNow();
                        _startedTimestamp[next] = _clock.GetTimestamp();
                        _running[next] = ExecuteStepAsync(next);
                    }

                    var stopped = linked.IsCancellationRequested;
                    if (stopped && drain is null)
                    {
                        drain = new CancellationTokenSource(_options.CancellationDrainTimeout, _clock);
                    }

                    if (inFlight.Count == 0)
                    {
                        break;
                    }

                    int index;
                    try
                    {
                        index = await _completions.Reader
                            .ReadAsync(drain?.Token ?? CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    if (index == Wake)
                    {
                        continue;
                    }

                    inFlight.Remove(index);
                    _running.Remove(index);
                    if (stopped || _state.Read(index) is not { Status: StepStatus.Succeeded })
                    {
                        continue;
                    }

                    foreach (var dependent in _plan.Dependents[index])
                    {
                        if (--remaining[dependent] == 0)
                        {
                            ready.Add(dependent);
                        }
                    }
                }
            }
            finally
            {
                drain?.Dispose();
            }

            var result = BuildResult(ct, timeoutCts, inFlight, startedAt, startedTimestamp);
            if (inFlight.Count == 0)
            {
                _state.Dispose();
            }
            else
            {
                // Steps that ignored the drain timeout still hold on to this state; release their parsed JSON
                // documents only once they have finished so a late binding read never touches disposed data.
                var pending = new Task[inFlight.Count];
                var position = 0;
                foreach (var index in inFlight)
                {
                    pending[position++] = _running[index];
                }

                _ = DisposeStateAsync(_state, pending);
            }

            return result;
        }

        private static async Task DisposeStateAsync(RunState state, Task[] pending)
        {
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch
            {
                // Step failures are already recorded; this task only releases resources.
            }

            state.Dispose();
        }

        private WorkflowRunResult BuildResult(
            CancellationToken ct,
            CancellationTokenSource? timeoutCts,
            HashSet<int> inFlight,
            DateTimeOffset startedAt,
            long startedTimestamp)
        {
            var steps = _plan.Steps;
            var results = new StepResult[steps.Length];
            var neverRan = new List<string>();
            StepResult? firstFailure = null;
            var failed = false;

            for (var i = 0; i < steps.Length; i++)
            {
                var step = steps[i];
                var outcome = _state.Read(i);
                StepStatus status;
                string? content = null;
                string? error = null;
                var isError = false;
                var stepStartedAt = _startedAt[i];
                var stepCompletedAt = _completedAt[i];
                var stepDuration = _duration[i];

                if (outcome is not null)
                {
                    status = outcome.Status;
                    content = outcome.Content;
                    error = outcome.Error;
                    isError = outcome.IsError;
                }
                else
                {
                    stepStartedAt = default;
                    stepCompletedAt = default;
                    stepDuration = TimeSpan.Zero;
                    if (inFlight.Contains(i))
                    {
                        status = StepStatus.Cancelled;
                        error = $"step '{step.Id}' did not stop within the {_options.CancellationDrainTimeout.TotalSeconds:0.###}s cancellation drain timeout";
                    }
                    else if (HasFailedAncestor(i))
                    {
                        status = StepStatus.Skipped;
                        error = $"step '{step.Id}' was not run because a step it depends on failed";
                    }
                    else
                    {
                        status = StepStatus.NotRun;
                        error = $"step '{step.Id}' was not run";
                    }
                }

                var stepResult = new StepResult
                {
                    Index = i,
                    Id = step.Id,
                    Tool = step.Tool,
                    Status = status,
                    Content = content,
                    IsError = isError,
                    Error = error,
                    StartedAt = stepStartedAt,
                    CompletedAt = stepCompletedAt,
                    Duration = stepDuration,
                    DependsOn = step.DependsOn,
                };

                results[i] = stepResult;
                if (status == StepStatus.Failed)
                {
                    failed = true;
                    firstFailure ??= stepResult;
                }

                if (status is StepStatus.NotRun or StepStatus.Skipped or StepStatus.Cancelled)
                {
                    neverRan.Add(step.Id);
                }
            }

            var ranCount = results.Length - neverRan.Count;
            WorkflowRunStatus runStatus;
            string? runError;
            if (ct.IsCancellationRequested)
            {
                runStatus = WorkflowRunStatus.Cancelled;
                runError = $"run cancelled after {ranCount} of {results.Length} steps";
            }
            else if (timeoutCts is { IsCancellationRequested: true })
            {
                runStatus = WorkflowRunStatus.TimedOut;
                runError = $"workflow did not finish within {_options.Timeout!.Value.TotalSeconds:0.###}s; {ranCount} of {results.Length} steps ran";
            }
            else if (failed)
            {
                runStatus = WorkflowRunStatus.Failed;
                runError = firstFailure?.Error ?? "a step failed";
            }
            else if (neverRan.Count > 0)
            {
                runStatus = WorkflowRunStatus.Failed;
                runError = "some steps could not be scheduled";
            }
            else
            {
                runStatus = WorkflowRunStatus.Completed;
                runError = null;
            }

            var completedAt = _clock.GetUtcNow();
            return new WorkflowRunResult
            {
                Status = runStatus,
                Name = _plan.Name,
                Steps = results,
                Diagnostics = [],
                Error = runError,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                Duration = _clock.GetElapsedTime(startedTimestamp, _clock.GetTimestamp()),
                NeverRanStepIds = neverRan.ToArray(),
            };
        }

        private bool HasFailedAncestor(int index)
        {
            var steps = _plan.Steps;
            var visited = new bool[steps.Length];
            var stack = new Stack<int>();
            stack.Push(index);
            visited[index] = true;
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                foreach (var dependency in steps[node].DependencyIndices)
                {
                    if (_state.Read(dependency) is { Status: StepStatus.Failed })
                    {
                        return true;
                    }

                    if (visited[dependency])
                    {
                        continue;
                    }

                    visited[dependency] = true;
                    stack.Push(dependency);
                }
            }

            return false;
        }

        private async Task ExecuteStepAsync(int index)
        {
            var step = _plan.Steps[index];
            StepOutcome outcome;
            try
            {
                outcome = await RunStepAsync(step).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (IsRunCancellation(ex))
            {
                outcome = new StepOutcome(StepStatus.Cancelled, null, false, $"step '{step.Id}' was cancelled");
            }
            catch (Exception ex)
            {
                outcome = new StepOutcome(
                    StepStatus.Failed,
                    null,
                    false,
                    $"step '{step.Id}': {ex.GetType().Name}: {ex.Message}");
            }

            _completedAt[index] = _clock.GetUtcNow();
            _duration[index] = _clock.GetElapsedTime(_startedTimestamp[index], _clock.GetTimestamp());
            _state.Publish(index, outcome);
            _completions.Writer.TryWrite(index);
        }

        private bool IsRunCancellation(OperationCanceledException ex) =>
            ex.CancellationToken == _runToken
            || (ex.CancellationToken.IsCancellationRequested && _runToken.IsCancellationRequested);

        private async Task<StepOutcome> RunStepAsync(PlanStep step)
        {
            if (!TryResolveArguments(step, out var args, out var document, out var error))
            {
                return new StepOutcome(StepStatus.Failed, null, false, error);
            }

            try
            {
                var result = await _host.CallToolAsync(step.Tool, args, _runToken).ConfigureAwait(false);
                if (result.IsError)
                {
                    var detail = string.IsNullOrWhiteSpace(result.Content) ? string.Empty : $": {result.Content}";
                    return new StepOutcome(
                        StepStatus.Failed,
                        result.Content,
                        true,
                        $"step '{step.Id}': tool '{step.Tool}' reported an error{detail}");
                }

                return new StepOutcome(StepStatus.Succeeded, result.Content, false, null);
            }
            finally
            {
                document?.Dispose();
            }
        }

        private bool TryResolveArguments(
            PlanStep step,
            out JsonElement args,
            out JsonDocument? document,
            out string? error)
        {
            args = default;
            document = null;
            error = null;

            if (step.ArgsTemplate.IsPassThrough)
            {
                args = step.Args.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? EmptyArguments.RootElement
                    : step.Args;
                return true;
            }

            var buffer = new ArrayBufferWriter<byte>(256);
            var context = new ResolutionContext(_state);
            WriteOutcome outcome;
            using (var writer = new Utf8JsonWriter(buffer, ArgumentWriterOptions))
            {
                outcome = step.ArgsTemplate.Write(writer, null, context);
                if (outcome == WriteOutcome.Skipped)
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                    outcome = WriteOutcome.Written;
                }
            }

            if (outcome == WriteOutcome.Failed)
            {
                error = context.Error ?? $"step '{step.Id}': argument bindings could not be resolved";
                return false;
            }

            try
            {
                document = JsonDocument.Parse(buffer.WrittenMemory);
            }
            catch (JsonException ex)
            {
                error = $"step '{step.Id}': resolved arguments are not valid JSON ({ex.Message})";
                return false;
            }

            args = document.RootElement;
            return true;
        }
    }
}
