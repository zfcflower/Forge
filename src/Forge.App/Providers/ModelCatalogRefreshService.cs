using System.Diagnostics;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>Which of the three callers asked for a refresh; each has its own rules.</summary>
internal enum ModelCatalogRefreshMode
{
    /// <summary>The daily 05:00 run: always fetches (conditionally) and always refreshes the provider lists.</summary>
    Scheduled,

    /// <summary>The settings page's "update now": does nothing when today's catalog is already here.</summary>
    Manual,

    /// <summary>The background check on startup: snapshot only, and only when today's is missing.</summary>
    Startup,
}

internal enum ModelCatalogRefreshOutcome
{
    /// <summary>A new catalog was written.</summary>
    Updated,

    /// <summary>The source answered 304; what is on disk is still the newest.</summary>
    NotModified,

    /// <summary>Nothing to do: today's catalog was already fetched, or another refresh is running.</summary>
    Skipped,

    /// <summary>No catalog: the previous one stays in place.</summary>
    Failed,
}

/// <summary>What one refresh did, for the run history and the settings row.</summary>
internal sealed record ModelCatalogRefreshResult(
    ModelCatalogRefreshOutcome Outcome,
    int ProviderCount = 0,
    int ModelCount = 0,
    int ProviderLists = 0,
    string? Failure = null,
    IReadOnlyList<string>? ProviderProblems = null);

/// <summary>
/// The one place the model catalog is refreshed: fetch (conditionally, with the stored ETag), store, then refresh every
/// connected provider's own model list. The daily automation, the startup check and the settings button all come through
/// here, so the "already fetched today" rule and the single-flight guard exist once.
/// </summary>
/// <remarks>
/// <para>
/// The rules, in one place: the daily run always asks the source (a 304 costs a few hundred bytes) and always refreshes the
/// provider lists; the manual and startup paths skip entirely when today's snapshot is already on disk. Provider lists are
/// skipped on the startup path because they need a credential per provider and the window is still starting.
/// </para>
/// <para>
/// A provider whose list call fails never fails the refresh: the run records how many succeeded and carries the first
/// sentences of the ones that did not, and the catalog that did arrive is kept either way.
/// </para>
/// </remarks>
internal sealed class ModelCatalogRefreshService
{
    private readonly ProviderServices providers;
    private readonly HttpClient http;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile bool busy;

    public ModelCatalogRefreshService(ProviderServices providers, HttpClient http, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(http);
        this.providers = providers;
        this.http = http;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when a refresh starts or ends; the UI marshals to its own thread.</summary>
    public event EventHandler? Changed;

    /// <summary>True while a refresh is running (the settings row shows it).</summary>
    public bool IsBusy => busy;

    /// <summary>The last result of this process, or <c>null</c> when nothing ran yet.</summary>
    public ModelCatalogRefreshResult? LastResult { get; private set; }

    /// <summary>Completes once the cached snapshot has been read from disk; see <see cref="ModelCatalogStore.Loaded"/>.</summary>
    public Task Loaded => providers.ModelCatalog.Loaded;

    /// <summary>True when the cached snapshot was fetched on today's local date.</summary>
    public bool IsFromToday => providers.ModelCatalog.IsFromToday(Zone);

    private static TimeZoneInfo Zone => TimeZoneInfo.Local;

    /// <summary>
    /// Refreshes according to <paramref name="mode"/>. Never throws for a network or shape problem — the result says what
    /// happened — and cancelling the token cancels the download and the list calls.
    /// </summary>
    public async Task<ModelCatalogRefreshResult> RefreshAsync(ModelCatalogRefreshMode mode, CancellationToken ct)
    {
        // Single flight: the startup check, the daily run and a user pressing the button can all meet here.
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return Finish(new ModelCatalogRefreshResult(ModelCatalogRefreshOutcome.Skipped));
        }

        busy = true;
        RaiseChanged();
        try
        {
            if (mode is not ModelCatalogRefreshMode.Scheduled && IsFromToday)
            {
                return Finish(new ModelCatalogRefreshResult(ModelCatalogRefreshOutcome.Skipped));
            }

            var current = providers.ModelCatalog.View.Snapshot;
            var sourceUrl = providers.Catalog.ModelCatalogSourceUrl;
            var now = time.GetUtcNow();
            // A validator belongs to the source that issued it: after the address changed, the old ETag must not travel to
            // the new host (a 304 for a validator it never issued would pin the app to the old source's data forever).
            var etag = current is not null && string.Equals(current.SourceUrl, sourceUrl, StringComparison.Ordinal)
                ? current.ETag
                : null;
            var fetched = await ModelCatalogFetcher.FetchAsync(http, sourceUrl, etag, now, ct: ct).ConfigureAwait(false);

            switch (fetched.Status)
            {
                case ModelCatalogFetchStatus.Failed:
                    Trace.TraceWarning("[model-catalog] refresh failed: " + fetched.Message);
                    return Finish(new ModelCatalogRefreshResult(ModelCatalogRefreshOutcome.Failed, Failure: fetched.Message));

                case ModelCatalogFetchStatus.Updated when fetched.Snapshot is { } snapshot:
                    providers.ModelCatalog.Apply(snapshot);
                    break;

                case ModelCatalogFetchStatus.NotModified when current is { } unchanged:
                    // Nothing new, but the source was asked: record the check so "today's catalog is here" stays true.
                    providers.ModelCatalog.Apply(unchanged with { CheckedAt = now });
                    break;
            }

            var updated = fetched.Status == ModelCatalogFetchStatus.Updated;
            var view = providers.ModelCatalog.View;
            var lists = mode == ModelCatalogRefreshMode.Startup
                ? (Refreshed: 0, Problems: (IReadOnlyList<string>)Array.Empty<string>())
                : await RefreshProviderListsAsync(ct).ConfigureAwait(false);

            return Finish(new ModelCatalogRefreshResult(
                updated ? ModelCatalogRefreshOutcome.Updated : ModelCatalogRefreshOutcome.NotModified,
                view.ProviderCount,
                view.ModelCount,
                lists.Refreshed,
                ProviderProblems: lists.Problems));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.TraceWarning("[model-catalog] refresh threw: " + exception.Message);
            return Finish(new ModelCatalogRefreshResult(ModelCatalogRefreshOutcome.Failed, Failure: exception.Message));
        }
        finally
        {
            busy = false;
            gate.Release();
            RaiseChanged();
        }
    }

    /// <summary>
    /// Asks every connected provider that supports listing for its model list. A provider that is not ready is skipped —
    /// calling it would only produce the "no key" sentence the settings row already shows.
    /// </summary>
    private async Task<(int Refreshed, IReadOnlyList<string> Problems)> RefreshProviderListsAsync(CancellationToken ct)
    {
        var refreshed = 0;
        List<string>? problems = null;
        foreach (var provider in providers.Catalog.Providers)
        {
            if (!provider.Definition.Preset.SupportsModelListing || provider.Status.Kind != ProviderStatusKind.Ready)
            {
                continue;
            }

            try
            {
                await providers.RefreshModelsAsync(provider, ct).ConfigureAwait(false);
                refreshed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Trace.TraceWarning("[model-catalog] model list of " + provider.Id + " failed: " + exception.Message);
                (problems ??= []).Add(provider.DisplayName + ": " + exception.Message);
            }
        }

        return (refreshed, problems ?? []);
    }

    private ModelCatalogRefreshResult Finish(ModelCatalogRefreshResult result)
    {
        LastResult = result;
        return result;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
