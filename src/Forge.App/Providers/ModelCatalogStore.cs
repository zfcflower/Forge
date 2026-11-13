using System.Diagnostics;
using Forge.Core.Providers;
using Forge.IO;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Providers;

/// <summary>
/// The fetched model catalog as the app keeps it: one small file (<c>model-catalog.json</c>) under the data root holding the
/// last snapshot, and the <see cref="ModelCatalogView"/> every reader resolves against.
/// </summary>
/// <remarks>
/// <para>
/// It is a cache of somebody else's published list, not configuration: it lives beside <c>settings.json</c> rather than in
/// it, it can be deleted at any time without losing a user's choice, and it is replaced whole by each successful refresh.
/// </para>
/// <para>
/// The file is read on the pool at construction, so a cold catalog costs the startup nothing but a background task; until it
/// lands the view is <see cref="ModelCatalogView.Empty"/> and the resolution chain simply falls through to the built-in
/// table. Readers take the current view by reference, so a refresh mid-session is visible to the next metadata question
/// without rebuilding any catalogue.
/// </para>
/// </remarks>
internal sealed class ModelCatalogStore
{
    public const string FileName = "model-catalog.json";

    private readonly string path;
    private readonly TimeProvider time;
    private volatile ModelCatalogView view = ModelCatalogView.Empty;
    private readonly Task loaded;
    private volatile string? loadProblem;
    private volatile bool loadFinished;

    public ModelCatalogStore(string dataDirectory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        path = System.IO.Path.Combine(dataDirectory, FileName);
        this.time = time ?? TimeProvider.System;
        loaded = Task.Run(LoadFromDisk);
    }

    /// <summary>
    /// Completes when the file has been read (or found missing/damaged). The startup catch-up waits on it: a decision taken
    /// while the read is still running would see "no catalog" and fetch a snapshot the disk already holds.
    /// </summary>
    public Task Loaded => loaded;

    public string Path => path;

    /// <summary>The catalog every metadata lookup resolves against; replaced whole after each refresh.</summary>
    public ModelCatalogView View => view;

    /// <summary>Why the file on disk could not be read, or <c>null</c> (also when there simply is no file yet).</summary>
    public string? LoadProblem => loadProblem;

    /// <summary>Raised on the thread that finished the load or the refresh; every UI reader marshals it (see the settings page).</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Reads the file on the calling thread. Construction reads on the pool; a caller that must have the catalog before it
    /// continues (a test, or a future synchronous startup path) uses this.
    /// </summary>
    internal void LoadNow()
    {
        LoadFromDisk();
        loaded.GetAwaiter().GetResult();
    }

    /// <summary>The clock the "is today's catalog already here?" test uses, and the fetch timestamp.</summary>
    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>True once the constructor's background read has finished (found, missing or damaged).</summary>
    public bool IsLoaded => loadFinished;

    /// <summary>True when the cached snapshot was fetched on today's local date.</summary>
    public bool IsFromToday(TimeZoneInfo zone) => view.Snapshot is { } snapshot
        && snapshot.IsFromDay(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, zone).DateTime), zone);

    /// <summary>The local date the cached snapshot was fetched on, or <c>null</c>.</summary>
    public DateOnly? FetchedOn(TimeZoneInfo zone) => view.Snapshot?.LocalDate(zone);

    /// <summary>
    /// Stores a freshly fetched snapshot: the file first, then the live view, so a failed write leaves the app resolving
    /// against what it already had instead of a catalog that is not on disk.
    /// </summary>
    public void Apply(ModelCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        AtomicFile.WriteAllBytes(path, ModelCatalogJson.Write(snapshot));
        view = ModelCatalogView.Create(snapshot);
        loadProblem = null;
        loadFinished = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void LoadFromDisk()
    {
        try
        {
            if (File.Exists(path))
            {
                var payload = File.ReadAllBytes(path);
                if (ModelCatalogJson.TryRead(payload, out var snapshot, out var error) && snapshot is not null)
                {
                    view = ModelCatalogView.Create(snapshot);
                    loadProblem = null;
                }
                else
                {
                    // A damaged cache is not worth a startup failure: the next refresh replaces it whole.
                    loadProblem = error;
                    AppLogs.ModelCatalog.ZLogWarning($"{error}");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            loadProblem = exception.Message;
            AppLogs.ModelCatalog.ZLogWarning($"read failed: {exception.Message}");
        }
        finally
        {
            loadFinished = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
