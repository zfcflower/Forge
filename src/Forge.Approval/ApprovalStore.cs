using Microsoft.Data.Sqlite;

namespace Forge.Approval;

/// <summary>
/// Approval records for every plugin version, persisted in <c>&lt;directory&gt;/approvals.db</c> (SQLite, WAL,
/// <c>synchronous=FULL</c>). Every mutation is one <c>BEGIN IMMEDIATE</c> transaction that reads the row it changes and
/// upserts it back, so two hosts on one directory cannot lose each other's updates and a record is published to the
/// in-memory snapshot only after its commit succeeded. Reads are served from that snapshot and revalidated with
/// <c>PRAGMA data_version</c>, so a commit made by another process becomes visible without a reload. All members are
/// thread-safe; the records themselves are immutable.
/// </summary>
public sealed class ApprovalStore : IDisposable
{
    /// <summary>File name of the approval database inside the store directory.</summary>
    public const string FileName = "approvals.db";

    /// <summary>File name of the single-file state an earlier version wrote and this store imports once.</summary>
    public const string LegacyFileName = "approvals.json";

    /// <summary>Suffix the imported legacy file is moved to; the import marker, not this rename, prevents re-imports.</summary>
    public const string MigratedSuffix = ".migrated";

    private static readonly Comparison<PluginRecord> OrderByPlugin = static (left, right) =>
    {
        var byId = string.CompareOrdinal(left.PluginId, right.PluginId);
        return byId != 0 ? byId : string.CompareOrdinal(left.Version, right.Version);
    };

    private readonly Lock gate = new();
    private readonly SqliteConnection connection;
    private readonly string databasePath;
    private readonly string legacyPath;

    private Dictionary<(string PluginId, string Version), PluginRecord> records;
    private long dataVersion;
    private bool disposed;

#if DEBUG
    private readonly Action? afterInitialSnapshotRead;
    private readonly Action? snapshotRead;
#endif

    public ApprovalStore(string directory)
        : this(directory, null)
    {
    }

    /// <summary>
    /// Opens (or creates) the database in <paramref name="directory"/>. Throws <see cref="InvalidDataException"/> when
    /// the database cannot be opened, its schema is newer than this build, it holds an unknown status, or the legacy
    /// <c>approvals.json</c> file that was about to be imported is unreadable.
    /// </summary>
    public ApprovalStore(string directory, ApprovalStoreOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        options ??= new ApprovalStoreOptions();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.BusyTimeout, TimeSpan.Zero);

        var root = Path.GetFullPath(directory);
        databasePath = Path.Combine(root, FileName);
        legacyPath = Path.Combine(root, LegacyFileName);
        JournalMode = ApprovalDatabase.RollbackJournalMode;

#if DEBUG
        afterInitialSnapshotRead = options.AfterInitialSnapshotRead;
        snapshotRead = options.SnapshotRead;
#endif

        SqliteConnection? opened = null;
        try
        {
            Directory.CreateDirectory(root);
            opened = ApprovalDatabase.Open(databasePath, options, out var journalMode);
            var imported = ApprovalMigration.EnsureSchema(opened, databasePath, legacyPath, options);
            var version = ApprovalDatabase.DataVersion(opened);
            var loaded = ReadSnapshot(opened, null);
#if DEBUG
            afterInitialSnapshotRead?.Invoke();
#endif

            connection = opened;
            records = loaded;
            dataVersion = version;
            JournalMode = journalMode;
            if (!string.Equals(journalMode, ApprovalDatabase.WalJournalMode, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"forge-approval: '{databasePath}' cannot run in WAL mode ('{journalMode}'); writes use the rollback journal.");
            }

            if (imported)
            {
                TryMigrateLegacyFile();
            }
        }
        catch (SqliteException exception)
        {
            opened?.Dispose();
            throw new InvalidDataException($"Approval database '{databasePath}' is unusable: {exception.Message}", exception);
        }
        catch
        {
            opened?.Dispose();
            throw;
        }
    }

    /// <summary>Full path of the approval database this store reads and writes.</summary>
    public string DatabasePath => databasePath;

    /// <summary>Journal mode SQLite selected for this database: <c>wal</c>, or <c>delete</c> where WAL is unavailable.</summary>
    public string JournalMode { get; }

    public int Count
    {
        get
        {
            lock (gate)
            {
                ThrowIfDisposed();
                Refresh();
                return records.Count;
            }
        }
    }

    /// <summary>
    /// Counts the approval records in <paramref name="databasePath"/> over a read-only, unpooled connection that never
    /// writes: a missing file returns zero without opening anything, and so does an existing database without the
    /// <c>plugin_approval</c> table (probed in <c>sqlite_master</c> first). Any other SQLite failure propagates, so a
    /// corrupt or locked database is reported instead of being shown as "no plugins".
    /// </summary>
    public static async Task<int> CountReadOnlyAsync(string databasePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
        {
            return 0;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var probe = connection.CreateCommand();
            probe.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'plugin_approval'";
            var found = await probe.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (found is not long tables || tables == 0)
            {
                return 0;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM plugin_approval";
            var count = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return count is long records ? (int)records : 0;
        }
        catch (FileNotFoundException)
        {
            return 0;
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

        var stored = Change(record.PluginId, record.Version, cancellationToken, current =>
        {
            var now = DateTimeOffset.UtcNow;
            var createdAt = record.CreatedAt;
            if (createdAt == default)
            {
                createdAt = current is null ? now : current.CreatedAt;
            }

            if (createdAt == default)
            {
                createdAt = now;
            }

            return record with
            {
                CreatedAt = createdAt,
                UpdatedAt = record.UpdatedAt == default ? now : record.UpdatedAt,
            };
        });
        return stored!;
    }

    /// <summary>One version's record, or the most recently updated version when <paramref name="version"/> is null.</summary>
    public PluginRecord? Get(string pluginId, string? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        lock (gate)
        {
            ThrowIfDisposed();
            Refresh();
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
            ThrowIfDisposed();
            Refresh();
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

        return Change(pluginId, version, cancellationToken, current =>
        {
            var now = DateTimeOffset.UtcNow;
            var record = current ?? new PluginRecord
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

            return record with
            {
                SourceHash = sourceHash,
                DependencyHash = dependencyHash,
                SdkVersion = sdkVersion,
                ApprovedHash = Hashing.ApprovalFingerprint(sourceHash, dependencyHash, sdkVersion),
                Status = status,
                CreatedAt = record.CreatedAt == default ? now : record.CreatedAt,
                UpdatedAt = now,
            };
        });
    }

    /// <summary>
    /// Drops the approval and returns the version to <see cref="PluginStatus.Draft"/>. A
    /// <see cref="PluginStatus.Disabled"/> or <see cref="PluginStatus.Uninstalled"/> record is left untouched, so
    /// revoking never lifts a block.
    /// </summary>
    public PluginRecord? Revoke(string pluginId, string version, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(pluginId, version);

        return Change(pluginId, version, cancellationToken, current =>
        {
            if (current is null || current.Status is PluginStatus.Disabled or PluginStatus.Uninstalled)
            {
                return current;
            }

            return current with
            {
                ApprovedHash = null,
                Status = PluginStatus.Draft,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        });
    }

    /// <summary>Marks the version <see cref="PluginStatus.Disabled"/>; the approval hash is kept for <see cref="Enable"/>.</summary>
    public PluginRecord? Disable(string pluginId, string version, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(pluginId, version);

        return Change(pluginId, version, cancellationToken, current => current is null
            ? null
            : current with { Status = PluginStatus.Disabled, UpdatedAt = DateTimeOffset.UtcNow });
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

        return Change(pluginId, version, cancellationToken, current =>
        {
            if (current is null || current.Status != PluginStatus.Disabled)
            {
                return current;
            }

            return current with
            {
                Status = string.IsNullOrEmpty(current.ApprovedHash) ? PluginStatus.Draft : PluginStatus.Approved,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        });
    }

    /// <summary>
    /// Re-reads the whole table, as a restart would. The rows are loaded into a fresh map that replaces the in-memory
    /// snapshot only once the read succeeded, so a failed reload keeps the previously loaded records.
    /// </summary>
    public void Reload()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            var version = ApprovalDatabase.DataVersion(connection);
            var loaded = ReadSnapshot(connection, null);
            records = loaded;
            dataVersion = version;
        }
    }

    /// <summary>Closes the connection so the database files can be deleted or moved.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                ApprovalDatabase.Checkpoint(connection);
            }
            catch (SqliteException)
            {
            }

            connection.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>
    /// Runs one read-modify-write in a single <c>BEGIN IMMEDIATE</c> transaction: the row is read inside the
    /// transaction, the transition decides the new value, and the upsert commits before the snapshot is touched. A
    /// transition that returns the record it was given — or <c>null</c> — writes nothing.
    /// </summary>
    private PluginRecord? Change(
        string pluginId,
        string version,
        CancellationToken cancellationToken,
        Func<PluginRecord?, PluginRecord?> transition)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            var key = (pluginId, version);
            using var transaction = connection.BeginTransaction(deferred: false);
            var current = ApprovalDatabase.ReadRow(connection, transaction, pluginId, version, databasePath);
            var record = transition(current);
            if (record is not null && !ReferenceEquals(record, current))
            {
                ApprovalDatabase.Write(connection, transaction, record);
                transaction.Commit();
                records[key] = record;
                return record;
            }

            transaction.Commit();
            return record;
        }
    }

    /// <summary>
    /// Reloads the snapshot when another connection committed since it was taken. The version is read before the rows
    /// so a commit that lands between the two reads can only cause one extra reload, never a missed one.
    /// </summary>
    private void Refresh()
    {
        var version = ApprovalDatabase.DataVersion(connection);
        if (version == dataVersion)
        {
            return;
        }

        records = ReadSnapshot(connection, null);
        dataVersion = version;
    }

    private Dictionary<(string PluginId, string Version), PluginRecord> ReadSnapshot(
        SqliteConnection source,
        SqliteTransaction? transaction)
    {
        var loaded = ApprovalDatabase.ReadAll(source, transaction, databasePath);
#if DEBUG
        snapshotRead?.Invoke();
#endif
        return loaded;
    }

    private void TryMigrateLegacyFile()
    {
        try
        {
            File.Move(legacyPath, legacyPath + MigratedSuffix, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private static PluginRecord[] OrderedSnapshot(Dictionary<(string PluginId, string Version), PluginRecord> source)
    {
        var snapshot = new PluginRecord[source.Count];
        source.Values.CopyTo(snapshot, 0);
        Array.Sort(snapshot, OrderByPlugin);
        return snapshot;
    }

    private static void ValidateIdentity(string pluginId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
    }
}
