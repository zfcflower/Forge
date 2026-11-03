namespace Forge.Approval;

/// <summary>Knobs of one <see cref="ApprovalStore"/> database.</summary>
public sealed record ApprovalStoreOptions
{
    public static readonly TimeSpan DefaultBusyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long one statement waits for another writer's lock before SQLite reports the database as busy. The connection
    /// string's own default timeout is the same value rounded up to whole seconds.
    /// </summary>
    public TimeSpan BusyTimeout { get; init; } = DefaultBusyTimeout;

    /// <summary>Whether a <see cref="ApprovalStore.LegacyFileName"/> file left by an earlier version is imported.</summary>
    public bool ImportLegacyJson { get; init; } = true;

#if DEBUG
    /// <summary>Debug-only test seam invoked after the constructor's full-table read and before the snapshot is assigned.</summary>
    internal Action? AfterInitialSnapshotRead { get; init; }

    /// <summary>Debug-only test seam invoked once after every full-table snapshot read (constructor, reload, refresh).</summary>
    internal Action? SnapshotRead { get; init; }
#endif
}
