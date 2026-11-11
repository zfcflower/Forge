using System.Globalization;
using Forge.Abstractions;
using Forge.App.Localization;

namespace Forge.App.Automations;

/// <summary>
/// <see cref="BuiltInAutomations.Housekeeping"/> (daily at 04:00): prunes stale worktree records of every repository project,
/// removes empty General scratch folders no session uses, trims the run history, and archives automation-created sessions
/// idle for <see cref="ArchiveAfter"/> that are not open. Every step is best effort: one failing step does not stop the others.
/// </summary>
internal sealed class HousekeepingRunner(
    IProjectRegistry projects,
    ISessionCatalog catalog,
    IAutomationRunStore runs,
    Func<string, CancellationToken, Task> pruneWorktrees,
    Func<IReadOnlySet<string>> openSessions,
    TimeProvider time) : IAutomationRunner
{
    public static readonly TimeSpan ArchiveAfter = TimeSpan.FromDays(7);

    public const int RunsPerAutomation = 200;

    public const int RunsTotal = 2000;

    public async Task<AutomationRunOutcome> RunAsync(IAutomationRunContext context, CancellationToken ct)
    {
        var all = await projects.ListAsync(ct).ConfigureAwait(false);
        var pruned = 0;
        foreach (var project in all)
        {
            if (project.Kind != ProjectKind.Repository)
            {
                continue;
            }

            try
            {
                await pruneWorktrees(project.RootPath, ct).ConfigureAwait(false);
                pruned++;
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
            {
                System.Diagnostics.Trace.WriteLine("[housekeeping] prune failed for " + project.RootPath + ": " + exception.Message);
            }
        }

        var sessions = await catalog.QueryAsync(new SessionQuery(), ct).ConfigureAwait(false);
        var folders = 0;
        if (all.FirstOrDefault(static p => p.Kind == ProjectKind.General) is { } general)
        {
            folders = RemoveEmptyScratchFolders(general.RootPath, sessions);
        }

        var trimmed = await runs.TrimAsync(RunsPerAutomation, RunsTotal, ct).ConfigureAwait(false);
        var archived = await ArchiveIdleAsync(sessions, ct).ConfigureAwait(false);
        return AutomationRunOutcome.Completed(AutomationStrings.HousekeepingSummary(
            pruned.ToString(CultureInfo.InvariantCulture),
            folders.ToString(CultureInfo.InvariantCulture),
            trimmed.ToString(CultureInfo.InvariantCulture),
            archived.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Deletes <c>scratch/*</c> folders that are empty and that no stored session (of any status) points at.</summary>
    internal static int RemoveEmptyScratchFolders(string generalRoot, IReadOnlyList<SessionInfo> sessions)
    {
        var scratchRoot = Path.Combine(generalRoot, "scratch");
        if (!Directory.Exists(scratchRoot))
        {
            return 0;
        }

        var used = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            used.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.WorkspacePath)));
        }

        var removed = 0;
        foreach (var folder in Directory.EnumerateDirectories(scratchRoot))
        {
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                if (used.Contains(full) || Directory.EnumerateFileSystemEntries(full).Any())
                {
                    continue;
                }

                Directory.Delete(full, recursive: false);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.WriteLine("[housekeeping] scratch folder kept: " + exception.Message);
            }
        }

        return removed;
    }

    private async Task<int> ArchiveIdleAsync(IReadOnlyList<SessionInfo> sessions, CancellationToken ct)
    {
        var cutoff = time.GetUtcNow() - ArchiveAfter;
        var open = openSessions();
        var archived = 0;
        foreach (var session in sessions)
        {
            if (session is not { Status: SessionStatus.Active, ParentSessionId: null, AutomationId: not null } ||
                session.LastActiveAt > cutoff ||
                open.Contains(session.Id))
            {
                continue;
            }

            if (await catalog.SetStatusAsync(session.Id, SessionStatus.Archived, ct).ConfigureAwait(false))
            {
                archived++;
            }
        }

        return archived;
    }
}
