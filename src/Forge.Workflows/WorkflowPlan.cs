using System.Text.Json;

namespace Forge.Workflows;

/// <summary>An executable step: definition data plus the compiled args template and resolved dependency edges.</summary>
internal sealed class PlanStep
{
    public required int Index { get; init; }

    public required string Id { get; init; }

    public required string Tool { get; init; }

    public required StepErrorMode OnError { get; init; }

    public required JsonElement Args { get; init; }

    public required ArgTemplate ArgsTemplate { get; init; }

    /// <summary>Dependency ids in resolution order (explicit <c>after</c> entries, or the implicit previous step).</summary>
    public required string[] DependsOn { get; init; }

    public required int[] DependencyIndices { get; init; }
}

internal sealed class WorkflowPlan
{
    public required string? Name { get; init; }

    public required PlanStep[] Steps { get; init; }

    /// <summary>Reverse edges: for each step, the steps that list it in <c>after</c>.</summary>
    public required int[][] Dependents { get; init; }
}

/// <summary>Compiles a definition into a <see cref="WorkflowPlan"/>, reporting every problem as a diagnostic.</summary>
internal static class WorkflowPlanBuilder
{
    /// <summary>Returns <see langword="null"/> when the definition has error diagnostics; never throws.</summary>
    public static WorkflowPlan? Build(WorkflowDefinition definition, List<WorkflowDiagnostic> diagnostics)
    {
        if (definition.Version != WorkflowDefinition.SupportedVersion)
        {
            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowDiagnosticSeverity.Error,
                WorkflowDiagnosticCode.UnsupportedVersion,
                $"workflow version {definition.Version} is not supported; this build implements version {WorkflowDefinition.SupportedVersion}"));
        }

        var count = definition.Steps.Count;
        var ids = new string[count];
        var tools = new string[count];
        var after = new string[count][];
        var dependencies = new int[count][];
        var args = new ArgTemplate[count];
        var indexById = new Dictionary<string, int>(count, StringComparer.Ordinal);
        var stepDiagnostics = new List<WorkflowDiagnostic>[count];
        for (var i = 0; i < count; i++)
        {
            stepDiagnostics[i] = [];
        }

        for (var i = 0; i < count; i++)
        {
            var step = definition.Steps[i];
            var id = step?.Id;
            if (string.IsNullOrWhiteSpace(id))
            {
                id = $"#{i}";
                stepDiagnostics[i].Add(new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Error,
                    WorkflowDiagnosticCode.MissingStepId,
                    $"steps[{i}] has no id",
                    i));
            }
            else if (!indexById.TryAdd(id, i))
            {
                stepDiagnostics[i].Add(new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Error,
                    WorkflowDiagnosticCode.DuplicateStepId,
                    $"step id '{id}' is already used by steps[{indexById[id]}]",
                    i,
                    id));
            }

            ids[i] = id;
        }

        var bindings = new List<RefArg>();
        var dependents = new int[count][];

        for (var i = 0; i < count; i++)
        {
            var step = definition.Steps[i];
            var id = ids[i];

            var tool = step?.Tool;
            if (string.IsNullOrWhiteSpace(tool))
            {
                tool = string.Empty;
                stepDiagnostics[i].Add(new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Error,
                    WorkflowDiagnosticCode.MissingTool,
                    $"step '{id}' has no tool",
                    i,
                    id));
            }

            tools[i] = tool;

            var (dependencyIds, dependencyIndices) = ResolveDependencies(step, i, ids, indexById, stepDiagnostics[i]);
            after[i] = dependencyIds;
            dependencies[i] = dependencyIndices;

            var rawArgs = step is null ? default : step.Args;
            args[i] = ArgCompiler.Compile(rawArgs, i, id, indexById, stepDiagnostics[i], bindings);
        }

        for (var i = 0; i < count; i++)
        {
            diagnostics.AddRange(stepDiagnostics[i]);
        }

        var dependentLists = new List<int>[count];
        for (var i = 0; i < count; i++)
        {
            dependentLists[i] = [];
        }

        for (var i = 0; i < count; i++)
        {
            foreach (var dependency in dependencies[i])
            {
                dependentLists[dependency].Add(i);
            }
        }

        for (var i = 0; i < count; i++)
        {
            dependents[i] = dependentLists[i].ToArray();
        }

        DetectCycles(dependencies, dependents, ids, diagnostics);
        ValidateBindings(bindings, dependencies, diagnostics);

        if (diagnostics.Exists(static diagnostic => diagnostic.Severity == WorkflowDiagnosticSeverity.Error))
        {
            return null;
        }

        var steps = new PlanStep[count];
        for (var i = 0; i < count; i++)
        {
            var step = definition.Steps[i];
            steps[i] = new PlanStep
            {
                Index = i,
                Id = ids[i],
                Tool = tools[i],
                OnError = step?.OnError ?? StepErrorMode.Fail,
                Args = step is null ? default : step.Args,
                ArgsTemplate = args[i],
                DependsOn = after[i],
                DependencyIndices = dependencies[i],
            };
        }

        return new WorkflowPlan
        {
            Name = definition.Name,
            Steps = steps,
            Dependents = dependents,
        };
    }

    private static (string[] Ids, int[] Indices) ResolveDependencies(
        WorkflowStep? step,
        int index,
        string[] ids,
        Dictionary<string, int> indexById,
        List<WorkflowDiagnostic> diagnostics)
    {
        var id = ids[index];
        var after = step?.After;
        if (after is null)
        {
            return index == 0
                ? (Array.Empty<string>(), Array.Empty<int>())
                : ([ids[index - 1]], [index - 1]);
        }

        var dependencyIds = new List<string>(after.Count);
        var dependencyIndices = new List<int>(after.Count);
        for (var j = 0; j < after.Count; j++)
        {
            var dependencyId = after[j];
            if (string.IsNullOrWhiteSpace(dependencyId))
            {
                diagnostics.Add(new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Error,
                    WorkflowDiagnosticCode.InvalidAfterEntry,
                    $"step '{id}': after[{j}] is empty",
                    index,
                    id));
                continue;
            }

            if (string.Equals(dependencyId, id, StringComparison.Ordinal))
            {
                diagnostics.Add(new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Error,
                    WorkflowDiagnosticCode.SelfDependency,
                    $"step '{id}' lists itself in after",
                    index,
                    id));
                continue;
            }

            if (!indexById.TryGetValue(dependencyId, out var dependencyIndex))
            {
                diagnostics.Add(new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Error,
                    WorkflowDiagnosticCode.UnknownDependency,
                    $"step '{id}' depends on unknown step '{dependencyId}'",
                    index,
                    id));
                continue;
            }

            if (dependencyIndices.Contains(dependencyIndex))
            {
                continue;
            }

            dependencyIds.Add(dependencyId);
            dependencyIndices.Add(dependencyIndex);
        }

        return (dependencyIds.ToArray(), dependencyIndices.ToArray());
    }

    private static void DetectCycles(
        int[][] dependencies,
        int[][] dependents,
        string[] ids,
        List<WorkflowDiagnostic> diagnostics)
    {
        var count = dependencies.Length;
        var indegree = new int[count];
        var queue = new Queue<int>(count);
        for (var i = 0; i < count; i++)
        {
            indegree[i] = dependencies[i].Length;
            if (indegree[i] == 0)
            {
                queue.Enqueue(i);
            }
        }

        var processed = 0;
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            processed++;
            foreach (var dependent in dependents[node])
            {
                if (--indegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        if (processed == count)
        {
            return;
        }

        var remaining = new bool[count];
        for (var i = 0; i < count; i++)
        {
            remaining[i] = indegree[i] > 0;
        }

        var reported = new bool[count];
        for (var i = 0; i < count; i++)
        {
            if (!remaining[i] || reported[i])
            {
                continue;
            }

            if (!TryFindCycle(i, dependencies, remaining, out var cycle))
            {
                reported[i] = true;
                continue;
            }

            var isLowest = true;
            foreach (var node in cycle)
            {
                if (node < i)
                {
                    isLowest = false;
                    break;
                }

                reported[node] = true;
            }

            if (!isLowest)
            {
                continue;
            }

            var rendered = new string[cycle.Count + 1];
            for (var j = 0; j < cycle.Count; j++)
            {
                rendered[j] = ids[cycle[j]];
            }

            rendered[cycle.Count] = ids[cycle[0]];
            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowDiagnosticSeverity.Error,
                WorkflowDiagnosticCode.DependencyCycle,
                $"dependency cycle: {string.Join(" -> ", rendered)}",
                i,
                ids[i]));
        }
    }

    private static bool TryFindCycle(int start, int[][] dependencies, bool[] remaining, out List<int> cycle)
    {
        cycle = [];
        var count = dependencies.Length;
        var parent = new int[count];
        Array.Fill(parent, -1);
        var visited = new bool[count];
        var queue = new Queue<int>();
        queue.Enqueue(start);
        visited[start] = true;
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            foreach (var dependency in dependencies[node])
            {
                if (!remaining[dependency])
                {
                    continue;
                }

                if (dependency == start)
                {
                    var reversed = new List<int>();
                    for (var cursor = node; cursor != -1; cursor = parent[cursor])
                    {
                        reversed.Add(cursor);
                    }

                    reversed.Reverse();
                    cycle = reversed;
                    return true;
                }

                if (visited[dependency])
                {
                    continue;
                }

                visited[dependency] = true;
                parent[dependency] = node;
                queue.Enqueue(dependency);
            }
        }

        return false;
    }

    private static void ValidateBindings(List<RefArg> bindings, int[][] dependencies, List<WorkflowDiagnostic> diagnostics)
    {
        foreach (var binding in bindings)
        {
            if (binding.ProducerIndex < 0)
            {
                diagnostics.Add(new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Error,
                    WorkflowDiagnosticCode.UnknownBindingStep,
                    $"step '{binding.ConsumerId}': binding '{binding.Path.Text}' names unknown step '{binding.Path.StepId}'",
                    binding.ConsumerIndex,
                    binding.ConsumerId));
                continue;
            }

            if (binding.ProducerIndex == binding.ConsumerIndex)
            {
                diagnostics.Add(new WorkflowDiagnostic(
                    WorkflowDiagnosticSeverity.Error,
                    WorkflowDiagnosticCode.BindingOnNonDependency,
                    $"step '{binding.ConsumerId}': binding '{binding.Path.Text}' reads its own output",
                    binding.ConsumerIndex,
                    binding.ConsumerId));
                continue;
            }

            if (IsAncestor(binding.ProducerIndex, binding.ConsumerIndex, dependencies))
            {
                continue;
            }

            diagnostics.Add(new WorkflowDiagnostic(
                WorkflowDiagnosticSeverity.Error,
                WorkflowDiagnosticCode.BindingOnNonDependency,
                $"step '{binding.ConsumerId}': binding '{binding.Path.Text}' reads '{binding.Path.StepId}', which is not one of its dependencies; add '{binding.Path.StepId}' to its after list",
                binding.ConsumerIndex,
                binding.ConsumerId));
        }
    }

    private static bool IsAncestor(int candidate, int step, int[][] dependencies)
    {
        var visited = new bool[dependencies.Length];
        var stack = new Stack<int>();
        stack.Push(step);
        visited[step] = true;
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var dependency in dependencies[node])
            {
                if (dependency == candidate)
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
}
