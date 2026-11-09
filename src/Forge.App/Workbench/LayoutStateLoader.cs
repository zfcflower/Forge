using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Forge.Abstractions;
using Forge.App.ViewModels;

namespace Forge.App.Workbench;

/// <summary>
/// Reads and writes the two workbench snapshots in <c>ui_state</c> behind <see cref="IUiStateStore"/>. A missing key, a
/// foreign version or unparsable JSON is an absent snapshot, never an exception; every call reaches the store from a
/// pool thread.
/// </summary>
internal sealed class LayoutStateLoader
{
    private const string LayoutKey = "layout";
    private const string OpenTabsKey = "open-tabs";

    private readonly IUiStateStore uiState;

    public LayoutStateLoader(IUiStateStore uiState)
    {
        ArgumentNullException.ThrowIfNull(uiState);
        this.uiState = uiState;
    }

    public async Task<LayoutSnapshot?> ReadLayoutAsync(CancellationToken ct)
    {
        var snapshot = await ReadAsync(LayoutKey, WorkbenchJsonContext.Default.LayoutSnapshot, ct).ConfigureAwait(false);
        return snapshot switch
        {
            { Version: LayoutSnapshot.CurrentVersion } => snapshot,
            { Version: 4 } => MigrateFromV4(snapshot),
            { Version: 3 } => MigrateFromV3(snapshot),
            _ => null,
        };
    }

    /// <summary>
    /// A v3 snapshot is still valid apart from the left column's split, which existed in that version only as a trial default:
    /// it goes back to the default share so the two panels start evenly divided, everything else carries over.
    /// </summary>
    private static LayoutSnapshot MigrateFromV3(LayoutSnapshot snapshot)
    {
        snapshot.ProjectSplitRatio = WorkbenchMetrics.DefaultProjectSplitRatio;
        return MigrateFromV4(snapshot);
    }

    /// <summary>
    /// A v4 snapshot could only describe the bottom group as "collapsed to a strip on Main's bottom edge". That strip is gone —
    /// the group shrinks to its title bar like any other panel — so the flag becomes the minimized state and the old field is
    /// retired with it.
    /// </summary>
    private static LayoutSnapshot MigrateFromV4(LayoutSnapshot snapshot)
    {
        snapshot.BottomMinimized = snapshot.BottomCollapsed;
        snapshot.BottomCollapsed = false;
        snapshot.Version = LayoutSnapshot.CurrentVersion;
        return snapshot;
    }

    public async Task<OpenTabsSnapshot?> ReadOpenTabsAsync(CancellationToken ct)
    {
        var snapshot = await ReadAsync(OpenTabsKey, WorkbenchJsonContext.Default.OpenTabsSnapshot, ct).ConfigureAwait(false);
        return snapshot is { Version: OpenTabsSnapshot.CurrentVersion } ? snapshot : null;
    }

    public Task WriteLayoutAsync(LayoutSnapshot snapshot, CancellationToken ct) =>
        WriteAsync(LayoutKey, LayoutSnapshot.CurrentVersion, snapshot, WorkbenchJsonContext.Default.LayoutSnapshot, ct);

    public Task WriteOpenTabsAsync(OpenTabsSnapshot snapshot, CancellationToken ct) =>
        WriteAsync(OpenTabsKey, OpenTabsSnapshot.CurrentVersion, snapshot, WorkbenchJsonContext.Default.OpenTabsSnapshot, ct);

    private async Task<T?> ReadAsync<T>(string key, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : class
    {
        try
        {
            var entry = await BackgroundRead.RunAsync(token => uiState.GetAsync(key, token), ct).ConfigureAwait(false);
            if (entry is null || entry.Version <= 0)
            {
                return null;
            }

            var json = entry.Json;
            return await BackgroundRead.RunAsync(_ => Task.FromResult(JsonSerializer.Deserialize(json, typeInfo)), ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            Trace.WriteLine("[layout] ignoring stored " + key + ": " + exception.Message);
            return null;
        }
    }

    private async Task WriteAsync<T>(string key, int version, T snapshot, JsonTypeInfo<T> typeInfo, CancellationToken ct)
        where T : class
    {
        try
        {
            var json = JsonSerializer.Serialize(snapshot, typeInfo);
            await BackgroundRead.RunAsync(token => uiState.PutAsync(key, version, json, token), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            Trace.WriteLine("[layout] save failed: " + exception.Message);
        }
    }
}
