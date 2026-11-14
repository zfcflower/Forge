using Forge.Abstractions;

namespace Forge.App.ViewModels;

/// <summary>
/// The one rule a group's status is derived by - never stored, because a group has no status of its own. Both panels that
/// show a plan group use it (the TODO panel and the plan panel), so the two can never disagree about what a group with one
/// paused step and two done ones is.
/// </summary>
internal static class TodoStatusRule
{
    /// <summary>
    /// All done, otherwise one in progress, otherwise one paused, otherwise pending. An empty set is pending: it has no
    /// done step to be finished by.
    /// </summary>
    /// <param name="steps">The steps of the group, in any order.</param>
    /// <param name="status">Reads one step's stored status; a static lambda at both call sites, so no closure is allocated.</param>
    internal static TodoStatus Derive<T>(IReadOnlyList<T> steps, Func<T, TodoStatus> status)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(status);
        if (steps.Count == 0)
        {
            return TodoStatus.Pending;
        }

        var allDone = true;
        var inProgress = false;
        var paused = false;
        foreach (var step in steps)
        {
            switch (status(step))
            {
                case TodoStatus.Done:
                    continue;
                case TodoStatus.InProgress:
                    inProgress = true;
                    break;
                case TodoStatus.Paused:
                    paused = true;
                    break;
            }

            allDone = false;
        }

        if (allDone)
        {
            return TodoStatus.Done;
        }

        return inProgress ? TodoStatus.InProgress : paused ? TodoStatus.Paused : TodoStatus.Pending;
    }
}
