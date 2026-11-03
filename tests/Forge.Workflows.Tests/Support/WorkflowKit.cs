using Forge.Plugin.Sdk;

namespace Forge.Workflows.Tests.Support;

internal static class WorkflowKit
{
    public static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    public static Task<WorkflowRunResult> RunAsync(
        string json,
        IHostFacade host,
        RunOptions? options = null,
        CancellationToken ct = default) =>
        new WorkflowEngine().RunAsync(WorkflowJson.Parse(json), host, options ?? new RunOptions(), ct);

    public static Task<WorkflowRunResult> RunAsync(
        WorkflowDefinition definition,
        IHostFacade host,
        RunOptions? options = null,
        CancellationToken ct = default) =>
        new WorkflowEngine().RunAsync(definition, host, options ?? new RunOptions(), ct);

    public static RunOptions Options(int maxConcurrency = RunOptions.DefaultMaxConcurrency, TimeProvider? clock = null, TimeSpan? timeout = null) =>
        new()
        {
            MaxConcurrency = maxConcurrency,
            TimeProvider = clock ?? TimeProvider.System,
            Timeout = timeout,
        };

    public static StepResult Step(WorkflowRunResult result, string id) =>
        result.FindStep(id) ?? throw new InvalidOperationException($"no step result '{id}'");

    public static Task<T> Within<T>(Task<T> task) => task.WaitAsync(Guard);

    public static Task Within(Task task) => task.WaitAsync(Guard);
}
