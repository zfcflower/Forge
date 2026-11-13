using System.Diagnostics;
using Forge.App.Localization;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Lifecycle;

/// <summary>
/// The ids of purged sessions whose files (artifacts, review marks and comments) are not known to be cleaned up yet
/// (7.5): one id per line in <c>&lt;UserData&gt;/purge-pending.txt</c>. The purge has already removed the session rows,
/// so this file is the only place such an id survives a restart. Every write replaces the file whole (temp file, then a
/// move), so a crash leaves the old or the new list, never half of one. Lines that cannot be a session id are ignored.
/// </summary>
internal sealed class PurgeCleanupJournal(string path)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public string FilePath => path;

    public async Task<IReadOnlyList<string>> ReadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadUnlockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task AddAsync(string sessionId, CancellationToken ct)
    {
        if (!IsSessionId(sessionId))
        {
            throw new ArgumentException($"'{sessionId}' is not a session id.", nameof(sessionId));
        }

        return UpdateAsync(
            ids =>
            {
                if (ids.Contains(sessionId, StringComparer.Ordinal))
                {
                    return false;
                }

                ids.Add(sessionId);
                return true;
            },
            ct);
    }

    public Task RemoveAsync(string sessionId, CancellationToken ct) =>
        UpdateAsync(ids => ids.RemoveAll(id => string.Equals(id, sessionId, StringComparison.Ordinal)) > 0, ct);

    /// <summary>
    /// The storage rule for a session id (Core's <c>StoragePaths.SafeSegment</c>): ASCII letters, digits, '-', '_' and '.',
    /// not only dots and not ending in a dot. An id the stores would refuse never enters the journal, so no line can fail
    /// every start for good.
    /// </summary>
    internal static bool IsSessionId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[^1] == '.')
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private async Task UpdateAsync(Func<List<string>, bool> change, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ids = await ReadUnlockedAsync(ct).ConfigureAwait(false);
            if (!change(ids))
            {
                return;
            }

            if (ids.Count == 0)
            {
                File.Delete(path);
                return;
            }

            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            await File.WriteAllLinesAsync(temp, ids, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<string>> ReadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        var ids = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (IsSessionId(line) && !ids.Contains(line, StringComparer.Ordinal))
            {
                ids.Add(line);
            }
            else if (line.Length > 0 && !IsSessionId(line))
            {
                AppLogs.Lifecycle.ZLogWarning($"ignored a line of '{path}' that is not a session id");
            }
        }

        return ids;
    }
}

/// <summary>
/// The file cleanup after a purge (7.5), with a retry that survives a restart: the id is written to the journal before
/// the files are deleted and taken off it once they are, so a failure (or a crash in between) leaves it for the next
/// start, which tries again. Deleting is by session id only and idempotent, so a retry never touches another session.
/// </summary>
internal sealed class PurgeCleanup(
    PurgeCleanupJournal journal,
    Func<string, CancellationToken, Task> deleteFiles,
    Func<string, CancellationToken, Task<bool>> sessionExists)
{
    /// <summary>The cleanup <see cref="RemovalFlow"/> runs per purged id; a failure is thrown (and the id kept for the retry).</summary>
    public async Task CleanUpAsync(string sessionId, CancellationToken ct)
    {
        Exception? unjournaled = null;
        try
        {
            await journal.AddAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The cleanup still runs; only its retry is lost, which the failure message says if it fails. An id the
            // journal refuses is one the stores refuse too, so that cleanup fails and says it is not retried.
            AppLogs.Lifecycle.ZLogWarning($"could not note '{sessionId}' in '{journal.FilePath}': {exception.Message}");
            unjournaled = exception;
        }

        try
        {
            await deleteFiles(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (unjournaled is not null && exception is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new IOException(exception.Message + "; " + RemovalStrings.PurgeNotRetried(unjournaled.Message), exception);
        }

        await ForgetAsync(sessionId).ConfigureAwait(false);
    }

    /// <summary>
    /// Startup: cleans up after every id the journal still holds and returns the failures (those ids stay for the next
    /// start). An id whose session row exists again was not purged by this journal's purge, so its files are not
    /// touched and the id is dropped.
    /// </summary>
    public async Task<IReadOnlyList<string>> RetryPendingAsync(CancellationToken ct)
    {
        var failures = new List<string>();
        foreach (var id in await journal.ReadAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await sessionExists(id, ct).ConfigureAwait(false))
                {
                    AppLogs.Lifecycle.ZLogInformation($"'{id}' exists again; its pending purge cleanup is dropped");
                }
                else
                {
                    await deleteFiles(id, ct).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                AppLogs.Lifecycle.ZLogWarning($"the purge cleanup of '{id}' failed again: {exception.Message}");
                failures.Add(id + ": " + exception.Message);
                continue;
            }

            await ForgetAsync(id).ConfigureAwait(false);
        }

        return failures;
    }

    /// <summary>Takes a cleaned-up id off the journal; failing that only repeats a cleanup that is harmless to repeat.</summary>
    private async Task ForgetAsync(string sessionId)
    {
        try
        {
            await journal.RemoveAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogs.Lifecycle.ZLogWarning($"could not take '{sessionId}' off '{journal.FilePath}': {exception.Message}");
        }
    }
}
