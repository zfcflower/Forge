using Forge.Abstractions;

namespace Forge.Core.Tests.Storage;

internal static class AutomationRuns
{
    public static readonly DateTimeOffset T0 = new(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

    public static AutomationRun Make(
        string id,
        string automationId = "auto-a",
        AutomationRunStatus status = AutomationRunStatus.Completed,
        int minute = 0,
        string? occurrence = null) =>
        new(
            id,
            automationId,
            "Name " + automationId,
            AutomationTriggerSource.Schedule,
            occurrence ?? "occ-" + id,
            status,
            T0.AddMinutes(minute));
}
