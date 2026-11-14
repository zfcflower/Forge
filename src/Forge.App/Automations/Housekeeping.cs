using System.Globalization;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Automations;

/// <summary>
/// <see cref="BuiltInAutomations.Housekeeping"/> (daily at 04:00): prunes stale worktree records of every repository project,
/// removes empty General scratch folders no session uses, trims the run history, and archives the root sessions the
/// configured <see cref="IdleArchiveSettings"/> selects (by default automation-created ones idle for
/// <see cref="ArchiveAfter"/>) that are not open. Every step is best effort: one failing step does not stop the others.
/// </summary>
internal sealed class HousekeepingRunner(
    IProjectRegistry projects,
    ISessionCatalog catalog,
    IAutomationRunStore runs,
    IAutomationDefinitionStore definitions,
    Func<string, CancellationToken, Task> pruneWorktrees,
    Func<IReadOnlySet<string>> openSessions,
    TimeProvider time,
    Func<IReadOnlyList<string>, Task>? archivedSessions = null,
    Func<IdleArchiveSettings>? archive = null) : IAutomationRunner
{
    /// <summary>The default idle window of the archive step; a configured <see cref="IdleArchiveSettings.After"/> replaces it.</summary>
    public static readonly TimeSpan ArchiveAfter = TimeSpan.FromDays(7);

    /// <summary>A scratch folder younger than this is never removed: a run that just created it may not have its catalog row yet.</summary>
    public static readonly TimeSpan ScratchMinAge = TimeSpan.FromHours(1);

    public const int RunsPerAutomation = 200;

    public const int RunsTotal = 2000;

    /// <summary>Read once per run, so a settings change applies from the next run. A null factory keeps the previous behaviour.</summary>
    private readonly Func<IdleArchiveSettings> idleArchive = archive ?? (static () => IdleArchiveSettings.Default);

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
                AppLogs.Housekeeping.ZLogWarning($"prune failed for {project.RootPath}: {exception.Message}");
            }
        }

        var sessions = await catalog.QueryAsync(new SessionQuery(), ct).ConfigureAwait(false);
        var folders = 0;
        if (all.FirstOrDefault(static p => p.Kind == ProjectKind.General) is { } general)
        {
            folders = await RemoveEmptyScratchAsync(general.RootPath, sessions, ct).ConfigureAwait(false);
        }

        var trimmed = await runs.TrimAsync(RunsPerAutomation, RunsTotal, ct).ConfigureAwait(false);
        var archived = await ArchiveIdleAsync(sessions, ct).ConfigureAwait(false);
        return AutomationRunOutcome.Completed(AutomationStrings.HousekeepingSummary(
            pruned.ToString(CultureInfo.InvariantCulture),
            folders.ToString(CultureInfo.InvariantCulture),
            trimmed.ToString(CultureInfo.InvariantCulture),
            archived.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Finds empty, old, unused scratch folders, then re-reads the catalog and deletes only those still unused: a scratch run
    /// that started during the scan gets its catalog row after our first snapshot.
    /// </summary>
    private async Task<int> RemoveEmptyScratchAsync(string generalRoot, IReadOnlyList<SessionInfo> sessions, CancellationToken ct)
    {
        var candidates = FindEmptyScratchFolders(generalRoot, sessions, time.GetUtcNow());
        if (candidates.Count == 0)
        {
            return 0;
        }

        var fresh = await catalog.QueryAsync(new SessionQuery(), ct).ConfigureAwait(false);
        return DeleteUnusedFolders(candidates, fresh, time.GetUtcNow());
    }

    /// <summary>Deletes <c>scratch/*</c> folders that are empty, older than <see cref="ScratchMinAge"/>, and that no stored session (of any status) points at.</summary>
    internal static int RemoveEmptyScratchFolders(string generalRoot, IReadOnlyList<SessionInfo> sessions, DateTimeOffset now) =>
        DeleteUnusedFolders(FindEmptyScratchFolders(generalRoot, sessions, now), sessions, now);

    private static HashSet<string> UsedPaths(IReadOnlyList<SessionInfo> sessions)
    {
        var used = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            used.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.WorkspacePath)));
        }

        return used;
    }

    private static bool IsYoung(string folder, DateTimeOffset now)
    {
        var newest = new DateTimeOffset(
            DateTime.SpecifyKind(
                Directory.GetCreationTimeUtc(folder) > Directory.GetLastWriteTimeUtc(folder) ? Directory.GetCreationTimeUtc(folder) : Directory.GetLastWriteTimeUtc(folder),
                DateTimeKind.Utc));
        return now - newest < ScratchMinAge;
    }

    private static List<string> FindEmptyScratchFolders(string generalRoot, IReadOnlyList<SessionInfo> sessions, DateTimeOffset now)
    {
        var found = new List<string>();
        var scratchRoot = Path.Combine(generalRoot, "scratch");
        if (!Directory.Exists(scratchRoot))
        {
            return found;
        }

        var used = UsedPaths(sessions);
        foreach (var folder in Directory.EnumerateDirectories(scratchRoot))
        {
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                if (!used.Contains(full) && !IsYoung(full, now) && !Directory.EnumerateFileSystemEntries(full).Any())
                {
                    found.Add(full);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLogs.Housekeeping.ZLogWarning($"scratch folder kept: {exception.Message}");
            }
        }

        return found;
    }

    private static int DeleteUnusedFolders(List<string> candidates, IReadOnlyList<SessionInfo> sessions, DateTimeOffset now)
    {
        var used = UsedPaths(sessions);
        var removed = 0;
        foreach (var full in candidates)
        {
            try
            {
                // Re-checked right before the delete; Directory.Delete(recursive: false) still refuses a folder that gained content.
                if (used.Contains(full) || IsYoung(full, now) || Directory.EnumerateFileSystemEntries(full).Any())
                {
                    continue;
                }

                Directory.Delete(full, recursive: false);
                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLogs.Housekeeping.ZLogWarning($"scratch folder kept: {exception.Message}");
            }
        }

        return removed;
    }

    /// <summary>Session ids that weekly (fixed-fork) automations reuse: <c>fork.session:&lt;definition id&gt;</c> state of every configured definition.</summary>
    private async Task<HashSet<string>?> ReferencedForkSessionsAsync(CancellationToken ct)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // The run store cannot list keys by prefix, so look up each configured definition's key.
            var load = await definitions.LoadAsync(ct).ConfigureAwait(false);
            foreach (var definition in load.Config.Definitions)
            {
                if (await runs.GetStateAsync(CatalogAutomationSessions.ForkStatePrefix + definition.Id, ct).ConfigureAwait(false) is { Length: > 0 } id)
                {
                    ids.Add(id);
                }
            }
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            AppLogs.Housekeeping.ZLogWarning($"fork references unreadable: {exception.Message}");
            return null;
        }

        return ids;
    }

    private async Task<int> ArchiveIdleAsync(IReadOnlyList<SessionInfo> sessions, CancellationToken ct)
    {
        var settings = idleArchive();
        if (!settings.Enabled)
        {
            return 0; // nothing to look up either: the fork references only matter to a step that archives
        }

        var cutoff = time.GetUtcNow() - settings.After;
        var open = openSessions();
        if (await ReferencedForkSessionsAsync(ct).ConfigureAwait(false) is not { } forks)
        {
            return 0; // cannot tell which sessions a weekly automation reuses: archive nothing rather than abandon its branch
        }

        var archived = new List<string>();
        foreach (var session in sessions)
        {
            // The automation-created condition is the scope; every other exemption stays as it was, whatever the scope.
            if (session is not { Status: SessionStatus.Active, ParentSessionId: null } ||
                (settings.Scope == IdleArchiveScope.AutomationSessions && session.AutomationId is null) ||
                session.LastActiveAt > cutoff ||
                open.Contains(session.Id) ||
                forks.Contains(session.Id))
            {
                continue;
            }

            if (await catalog.SetStatusAsync(session.Id, SessionStatus.Archived, ct).ConfigureAwait(false))
            {
                archived.Add(session.Id);
            }
        }

        // The sessions are not open, but terminals and runtimes may still be bound to them: the window applies the same
        // cleanup as a UI archive and refreshes the tree. Best effort: the archive itself already happened.
        if (archived.Count > 0 && archivedSessions is not null)
        {
            try
            {
                await archivedSessions(archived).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
            {
                AppLogs.Housekeeping.ZLogWarning($"archive cleanup failed: {exception.Message}");
            }
        }

        return archived.Count;
    }
}
