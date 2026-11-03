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

    private static readonly Comparison<PluginRecord> OrderByPlugin = static (left, right) =>
    {
        var byId = string.CompareOrdinal(left.PluginId, right.PluginId);
        return byId != 0 ? byId : string.CompareOrdinal(left.Version, right.Version);
    };

    private readonly Lock gate = new();
    private Dictionary<(string PluginId, string Version), PluginRecord> records;

    public ApprovalStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        FilePath = Path.Combine(root, FileName);
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
    public PluginRecord Upsert(PluginRecord record)
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
            Apply(key, stored);
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
        string sdkVersion)
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
            Apply(key, approved);
            return approved;
        }
    }

    /// <summary>
    /// Drops the approval and returns the version to <see cref="PluginStatus.Draft"/>. A
    /// <see cref="PluginStatus.Disabled"/> or <see cref="PluginStatus.Uninstalled"/> record is left untouched, so
    /// revoking never lifts a block.
    /// </summary>
    public PluginRecord? Revoke(string pluginId, string version)
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
            Apply(key, revoked);
            return revoked;
        }
    }

    /// <summary>Marks the version <see cref="PluginStatus.Disabled"/>; the approval hash is kept for <see cref="Enable"/>.</summary>
    public PluginRecord? Disable(string pluginId, string version)
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
            Apply(key, disabled);
            return disabled;
        }
    }

    /// <summary>
    /// Lifts <see cref="PluginStatus.Disabled"/>: back to <see cref="PluginStatus.Approved"/> when an approval hash is
    /// still recorded, otherwise to <see cref="PluginStatus.Draft"/>. Any other status is returned unchanged, so a
    /// <see cref="PluginStatus.Draft"/> record keeps needing approval even when it still carries an old hash and
    /// <see cref="PluginStatus.Uninstalled"/> stays uninstalled because enable cannot resurrect a removed plugin.
    /// </summary>
    public PluginRecord? Enable(string pluginId, string version)
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
            Apply(key, enabled);
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
    private void Apply((string PluginId, string Version) key, PluginRecord record)
    {
        var candidate = new Dictionary<(string PluginId, string Version), PluginRecord>(records)
        {
            [key] = record,
        };
        Persist(candidate);
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
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            using var stream = OpenForReading(FilePath);
            return JsonSerializer.Deserialize(stream, ApprovalJsonContext.Default.PluginRecordArray) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Approval file '{FilePath}' is not a valid plugin approval list.", exception);
        }
    }

    /// <summary>
    /// Opens the state file for reading, retrying while another store swaps the file in: Windows reports a file that
    /// is being replaced as access denied until the swap finished.
    /// </summary>
    private static FileStream OpenForReading(string path)
    {
        const int attempts = 8;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (UnauthorizedAccessException) when (attempt < attempts)
            {
                Thread.Sleep(1);
            }
        }
    }

    private void Persist(Dictionary<(string PluginId, string Version), PluginRecord> candidate)
    {
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

            Replace(temporary);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Replaces the state file with the finished temporary file. Windows refuses to replace a file while another
    /// handle still has it open, so the replacement is retried for up to two seconds before the failure surfaces.
    /// The temporary file is renamed onto the state file, which keeps the state file's name present for readers.
    /// </summary>
    private void Replace(string temporary)
    {
        const int timeoutMilliseconds = 2000;
        const int retryDelayMilliseconds = 1;
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (true)
        {
            try
            {
                File.Move(temporary, FilePath, overwrite: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw;
                }

                Thread.Sleep(retryDelayMilliseconds);
            }
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
