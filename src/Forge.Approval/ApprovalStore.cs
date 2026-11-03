using System.Diagnostics;
using System.Text.Json;

namespace Forge.Approval;

/// <summary>
/// Approval records for every plugin version, mirrored to <c>&lt;directory&gt;/approvals.json</c>. The file is the
/// authority across restarts: it is read once when the store is created, and every mutation rewrites it through a
/// temporary file plus a replace so a crash cannot leave a half-written state file. All members are thread-safe and
/// snapshot-based; the records themselves are immutable.
/// </summary>
public sealed class ApprovalStore
{
    public const string FileName = "approvals.json";

    private static readonly TimeSpan DefaultReplaceBudget = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReplaceSpinPhase = TimeSpan.FromMilliseconds(10);
    private static readonly Comparison<PluginRecord> OrderByPlugin = static (left, right) =>
    {
        var byId = string.CompareOrdinal(left.PluginId, right.PluginId);
        return byId != 0 ? byId : string.CompareOrdinal(left.Version, right.Version);
    };

    private const int RetryDelayMilliseconds = 1;
    private const int ReadAttempts = 8;
    private const int ReadSpinAttempts = 3;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    private readonly Lock gate = new();
    private readonly TimeSpan replaceBudget;
    private Dictionary<(string PluginId, string Version), PluginRecord> records;

    public ApprovalStore(string directory)
        : this(directory, DefaultReplaceBudget)
    {
    }

    /// <summary>Creates a store that gives up on an un-replaceable state file after <paramref name="replaceBudget"/>.</summary>
    /// <param name="replaceBudget">
    /// Total time <see cref="FilePath"/> may stay un-replaceable before the failing write is reported to the caller.
    /// </param>
    public ApprovalStore(string directory, TimeSpan replaceBudget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(replaceBudget, TimeSpan.Zero);
        var root = Path.GetFullPath(directory);
        FilePath = Path.Combine(root, FileName);
        this.replaceBudget = replaceBudget;
        records = ToMap(ReadFile());
    }

    /// <summary>Full path of the JSON state file this store reads and writes.</summary>
    public string FilePath { get; }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return records.Count;
            }
        }
    }

    /// <summary>
    /// Inserts or replaces one version's record. A supplied <see cref="PluginRecord.CreatedAt"/> wins; otherwise the
    /// existing creation time, or now, is kept. A default <see cref="PluginRecord.UpdatedAt"/> becomes now.
    /// </summary>
    public PluginRecord Upsert(PluginRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateIdentity(record.PluginId, record.Version);

        lock (gate)
        {
            var key = (record.PluginId, record.Version);
            var now = DateTimeOffset.UtcNow;
            var createdAt = record.CreatedAt;
            if (createdAt == default)
            {
                createdAt = records.TryGetValue(key, out var existing) ? existing.CreatedAt : now;
            }

            if (createdAt == default)
            {
                createdAt = now;
            }

            var stored = record with
            {
                CreatedAt = createdAt,
                UpdatedAt = record.UpdatedAt == default ? now : record.UpdatedAt,
            };
            Apply(key, stored, cancellationToken);
            return stored;
        }
    }

    /// <summary>One version's record, or the most recently updated version when <paramref name="version"/> is null.</summary>
    public PluginRecord? Get(string pluginId, string? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        lock (gate)
        {
            if (version is not null)
            {
                return records.TryGetValue((pluginId, version), out var exact) ? exact : null;
            }

            PluginRecord? latest = null;
            foreach (var record in records.Values)
            {
                if (!string.Equals(record.PluginId, pluginId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (latest is null ||
                    record.UpdatedAt > latest.UpdatedAt ||
                    (record.UpdatedAt == latest.UpdatedAt && string.CompareOrdinal(record.Version, latest.Version) > 0))
                {
                    latest = record;
                }
            }

            return latest;
        }
    }

    /// <summary>Every known record, ordered by plugin id then version.</summary>
    public IReadOnlyList<PluginRecord> List()
    {
        lock (gate)
        {
            return OrderedSnapshot(records);
        }
    }

    /// <summary>
    /// Approves the given content for one version: the hashes become the record's current content and their
    /// <see cref="Hashing.ApprovalFingerprint"/> becomes <see cref="PluginRecord.ApprovedHash"/>. A version without a
    /// record gets one, so a freshly discovered plugin can be approved in a single call. A
    /// <see cref="PluginStatus.Disabled"/> or <see cref="PluginStatus.Uninstalled"/> record keeps its status, so
    /// re-approval never lifts a block.
    /// </summary>
    public PluginRecord? SetApproved(
        string pluginId,
        string version,
        string sourceHash,
        string dependencyHash,
        string sdkVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(pluginId, version);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkVersion);

        lock (gate)
        {
            var key = (pluginId, version);
            var now = DateTimeOffset.UtcNow;
            var record = records.TryGetValue(key, out var existing)
                ? existing
                : new PluginRecord
                {
                    PluginId = pluginId,
                    Version = version,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

            var status = record.Status switch
            {
                PluginStatus.Disabled => PluginStatus.Disabled,
                PluginStatus.Uninstalled => PluginStatus.Uninstalled,
                _ => PluginStatus.Approved,
            };

            var approved = record with
            {
                SourceHash = sourceHash,
                DependencyHash = dependencyHash,
                SdkVersion = sdkVersion,
                ApprovedHash = Hashing.ApprovalFingerprint(sourceHash, dependencyHash, sdkVersion),
                Status = status,
                CreatedAt = record.CreatedAt == default ? now : record.CreatedAt,
                UpdatedAt = now,
            };
            Apply(key, approved, cancellationToken);
            return approved;
        }
    }

    /// <summary>
    /// Drops the approval and returns the version to <see cref="PluginStatus.Draft"/>. A
    /// <see cref="PluginStatus.Disabled"/> or <see cref="PluginStatus.Uninstalled"/> record is left untouched, so
    /// revoking never lifts a block.
    /// </summary>
    public PluginRecord? Revoke(string pluginId, string version, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(pluginId, version);

        lock (gate)
        {
            var key = (pluginId, version);
            if (!records.TryGetValue(key, out var record))
            {
                return null;
            }

            if (record.Status is PluginStatus.Disabled or PluginStatus.Uninstalled)
            {
                return record;
            }

            var revoked = record with
            {
                ApprovedHash = null,
                Status = PluginStatus.Draft,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            Apply(key, revoked, cancellationToken);
            return revoked;
        }
    }

    /// <summary>Marks the version <see cref="PluginStatus.Disabled"/>; the approval hash is kept for <see cref="Enable"/>.</summary>
    public PluginRecord? Disable(string pluginId, string version, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(pluginId, version);

        lock (gate)
        {
            var key = (pluginId, version);
            if (!records.TryGetValue(key, out var record))
            {
                return null;
            }

            var disabled = record with { Status = PluginStatus.Disabled, UpdatedAt = DateTimeOffset.UtcNow };
            Apply(key, disabled, cancellationToken);
            return disabled;
        }
    }

    /// <summary>
    /// Lifts <see cref="PluginStatus.Disabled"/>: back to <see cref="PluginStatus.Approved"/> when an approval hash is
    /// still recorded, otherwise to <see cref="PluginStatus.Draft"/>. Any other status is returned unchanged, so a
    /// <see cref="PluginStatus.Draft"/> record keeps needing approval even when it still carries an old hash and
    /// <see cref="PluginStatus.Uninstalled"/> stays uninstalled because enable cannot resurrect a removed plugin.
    /// </summary>
    public PluginRecord? Enable(string pluginId, string version, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(pluginId, version);

        lock (gate)
        {
            var key = (pluginId, version);
            if (!records.TryGetValue(key, out var record))
            {
                return null;
            }

            if (record.Status != PluginStatus.Disabled)
            {
                return record;
            }

            var enabled = record with
            {
                Status = string.IsNullOrEmpty(record.ApprovedHash) ? PluginStatus.Draft : PluginStatus.Approved,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            Apply(key, enabled, cancellationToken);
            return enabled;
        }
    }

    /// <summary>
    /// Re-reads the file, as a restart would. The records are loaded into a fresh map that replaces the in-memory
    /// state only once the read succeeded, so a failed reload keeps the previously loaded records.
    /// </summary>
    public void Reload()
    {
        lock (gate)
        {
            records = ToMap(ReadFile());
        }
    }

    /// <summary>
    /// Persists <paramref name="candidate"/> and only then publishes it, so a failed write leaves the in-memory state
    /// equal to what the file holds.
    /// </summary>
    private void Apply((string PluginId, string Version) key, PluginRecord record, CancellationToken cancellationToken)
    {
        var candidate = new Dictionary<(string PluginId, string Version), PluginRecord>(records)
        {
            [key] = record,
        };
        Persist(candidate, cancellationToken);
        records = candidate;
    }

    private static Dictionary<(string PluginId, string Version), PluginRecord> ToMap(PluginRecord[] source)
    {
        var map = new Dictionary<(string PluginId, string Version), PluginRecord>(source.Length);
        foreach (var record in source)
        {
            if (string.IsNullOrWhiteSpace(record.PluginId) || string.IsNullOrWhiteSpace(record.Version))
            {
                continue;
            }

            map[(record.PluginId, record.Version)] = record;
        }

        return map;
    }

    private static PluginRecord[] OrderedSnapshot(Dictionary<(string PluginId, string Version), PluginRecord> source)
    {
        var snapshot = new PluginRecord[source.Count];
        source.Values.CopyTo(snapshot, 0);
        Array.Sort(snapshot, OrderByPlugin);
        return snapshot;
    }

    private PluginRecord[] ReadFile()
    {
        using var stream = OpenForReading();
        if (stream is null)
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize(stream, ApprovalJsonContext.Default.PluginRecordArray) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Approval file '{FilePath}' is not a valid plugin approval list.", exception);
        }
    }

    /// <summary>
    /// Opens the state file for reading, retrying a bounded number of times while another store swaps the file in:
    /// Windows reports a file that is being replaced as access denied until the swap finished, and a file that
    /// momentarily disappeared reads as no state file at all, which is what <c>File.Exists</c> means for a fresh store.
    /// </summary>
    private FileStream? OpenForReading()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            try
            {
                return new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception exception) when (attempt < ReadAttempts && IsRetryableRead(exception))
            {
                if (attempt < ReadSpinAttempts)
                {
                    Thread.Yield();
                }
                else
                {
                    Thread.Sleep(RetryDelayMilliseconds);
                }
            }
        }
    }

    private bool IsRetryableRead(Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException || IsReplaceRace(exception);

    private void Persist(Dictionary<(string PluginId, string Version), PluginRecord> candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096))
            {
                JsonSerializer.Serialize(stream, OrderedSnapshot(candidate), ApprovalJsonContext.Default.PluginRecordArray);
                stream.Flush(flushToDisk: true);
            }

            Replace(temporary, cancellationToken);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Replaces the state file with the finished temporary file. Windows refuses to replace a file while another
    /// handle still has it open, so the replace is retried within <see cref="replaceBudget"/>: first by yielding the
    /// CPU, then by sleeping a millisecond at a time. The temporary file is renamed onto the state file, which keeps
    /// the state file's name present for readers.
    /// </summary>
    private void Replace(string temporary, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, FilePath, overwrite: true);
                return;
            }
            catch (Exception exception) when (IsReplaceRace(exception))
            {
                if (Stopwatch.GetElapsedTime(start) >= replaceBudget)
                {
                    throw;
                }

                if (Stopwatch.GetElapsedTime(start) < ReplaceSpinPhase)
                {
                    Thread.Yield();
                }
                else
                {
                    WaitForRetry(cancellationToken);
                }
            }
        }
    }

    private static void WaitForRetry(CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
        {
            _ = cancellationToken.WaitHandle.WaitOne(RetryDelayMilliseconds);
            return;
        }

        Thread.Sleep(RetryDelayMilliseconds);
    }

    /// <summary>
    /// True only for the Windows "state file temporarily not replaceable" race: the platform is Windows, the failure
    /// is an access denial or a sharing/lock violation, and the state file is still there, is not a directory and is
    /// not read-only. Anything else — a missing directory, a path that is a directory, a read-only file, a full disk,
    /// a real ACL denial — is reported without retrying.
    /// </summary>
    private bool IsReplaceRace(Exception exception)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var isRace = exception is UnauthorizedAccessException ||
            (exception is IOException io && (io.HResult & 0xFFFF) is SharingViolation or LockViolation);
        return isRace && IsReplaceableStateFile();
    }

    /// <summary>True when the state file exists, is not a directory and is not read-only.</summary>
    private bool IsReplaceableStateFile()
    {
        try
        {
            return (File.GetAttributes(FilePath) & (FileAttributes.Directory | FileAttributes.ReadOnly)) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateIdentity(string pluginId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
    }
}
