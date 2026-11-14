using System.Collections.ObjectModel;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Services;
using Forge.IO;
using ZLogger;

namespace Forge.App.ViewModels;

/// <summary>
/// The snapshot half of the palette: it turns the source snapshots into rows, and it is the only place that starts
/// background work. Every refresh carries a generation, so an answer that arrives after the user switched source (or
/// closed the palette) is dropped instead of overwriting a newer one.
/// </summary>
internal sealed partial class PaletteViewModel
{
    /// <summary>
    /// Quiet period before a content search starts. This is UI responsiveness, not a user preference: without it every
    /// keystroke would launch a workspace-wide search, and the answers would race each other.
    /// </summary>
    private const int ContentDebounceMilliseconds = 180;

    private string? excludedRaw;
    private IReadOnlyList<string> excluded = [];

    /// <summary>
    /// The current source's rows for the current query; the one place every state change funnels into. A query or source
    /// change starts the selection at the best row; a rebuild that only refreshes the snapshot behind the same list (a
    /// landing refresh, a language switch) keeps the row the user had.
    /// </summary>
    private void Rebuild(bool keepSelection = false) =>
        Replace(Build(PaletteQuery.ClampLimit(preferences.PalettePerSourceLimit)), keepSelection);

    private IReadOnlyList<PaletteItem> Build(int limit) => source switch
    {
        PaletteSource.Files => BuildFiles(limit),
        PaletteSource.Content => BuildContent(limit),
        PaletteSource.Sessions => BuildSessions(limit),
        PaletteSource.Commands => PaletteSources.Commands(host.Commands, effectiveQuery, limit),
        PaletteSource.Symbols => BuildSymbols(limit),
        _ => [],
    };

    private IReadOnlyList<PaletteItem> BuildFiles(int limit) =>
        string.IsNullOrEmpty(host.Workspace)
            ? [PaletteItem.Notice(PaletteSource.Files, Loc.Instance[PaletteStrings.NoticeNoWorkspace])]
            : PaletteSources.Files(files, effectiveQuery, limit);

    private IReadOnlyList<PaletteItem> BuildContent(int limit)
    {
        if (effectiveQuery.Length == 0)
        {
            return [];
        }

        if (contentProblem.Length > 0)
        {
            return [PaletteItem.Notice(PaletteSource.Content, contentProblem)];
        }

        if (!string.Equals(contentQuery, effectiveQuery, StringComparison.Ordinal))
        {
            // The answer on hand belongs to the previous query; the list stays empty until the new one lands.
            return [];
        }

        return PaletteSources.Content(contentResult, ExcludedDirectories(), limit);
    }

    private IReadOnlyList<PaletteItem> BuildSessions(int limit)
    {
        var rows = PaletteSources.Sessions(sessions, effectiveQuery, limit);
        return rows.Count == 0 && statusProblem.Length > 0
            ? [PaletteItem.Notice(PaletteSource.Sessions, statusProblem)]
            : rows;
    }

    private IReadOnlyList<PaletteItem> BuildSymbols(int limit)
    {
        if (symbols.Document.Length == 0)
        {
            return [PaletteItem.Notice(PaletteSource.Symbols, Loc.Instance[PaletteStrings.NoticeNoDocument])];
        }

        if (statusProblem.Length == 0 && symbols.Problem is { Length: > 0 } problem)
        {
            return [PaletteItem.Notice(PaletteSource.Symbols, problem)];
        }

        return statusProblem.Length > 0
            ? [PaletteItem.Notice(PaletteSource.Symbols, statusProblem)]
            : PaletteSources.Symbols(symbols, effectiveQuery, limit);
    }

    /// <summary>The configured excluded directory names, re-parsed only when the setting's text changed.</summary>
    private IReadOnlyList<string> ExcludedDirectories()
    {
        var raw = preferences.PaletteExcludeDirectories;
        if (!string.Equals(raw, excludedRaw, StringComparison.Ordinal))
        {
            excluded = Forge.App.Preferences.AppPreferences.ParseDirectoryNames(raw);
            excludedRaw = raw;
        }

        return excluded;
    }

    /// <summary>
    /// Replaces the rows. With <paramref name="keepSelection"/> the old selection is carried over to the equal row of the
    /// new list (a landing refresh must not move the row under the user's finger); otherwise - and whenever the old row is
    /// gone - the first navigable row is selected, so Enter is always meaningful after a keystroke.
    /// </summary>
    private void Replace(IReadOnlyList<PaletteItem> next, bool keepSelection)
    {
        var previous = selectedItem;
        rows = next;
        itemsView = new ReadOnlyObservableCollection<PaletteItem>(
            next.Count == 0 ? [] : new ObservableCollection<PaletteItem>(next));
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(StatusText));

        var kept = keepSelection && previous is not null
            ? next.FirstOrDefault(item => item.IsNavigable && item == previous)
            : null;
        SetSelection(kept ?? next.FirstOrDefault(static item => item.IsNavigable));
    }

    /// <summary>
    /// Selects <paramref name="item"/>. A rebuild recreates every row, so the preview is only reloaded when the new
    /// selection points somewhere else - otherwise typing would restart a file preview on every keystroke.
    /// </summary>
    private void SetSelection(PaletteItem? item)
    {
        if (ReferenceEquals(selectedItem, item))
        {
            return;
        }

        if (SameTarget(selectedItem, item))
        {
            // The same target in a row instance of the new list: point at that instance, because the old one is no longer
            // in Items and the list would then highlight nothing. The preview already shows this target, so it is not
            // reloaded.
            selectedItem = item;
            OnPropertyChanged(nameof(SelectedItem));
            return;
        }

        SelectedItem = item;
    }

    private static bool SameTarget(PaletteItem? left, PaletteItem? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.Source == right.Source && left.Kind == right.Kind && left.Line == right.Line &&
            string.Equals(left.Path, right.Path, StringComparison.Ordinal) &&
            string.Equals(left.CommandId, right.CommandId, StringComparison.Ordinal) &&
            string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
            string.Equals(left.Title, right.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// Test seam: installs an exact row list. A source never emits a list that mixes a notice with a result - a notice is
    /// the whole list - so the code that steps over rows cannot be proven for a mixed list through the palette's own
    /// surface. Everything after installing the rows goes through the public members.
    /// </summary>
    internal void InstallRowsForTest(IReadOnlyList<PaletteItem> next) => Replace(next, keepSelection: false);

    /// <summary>Starts the snapshot refresh of <paramref name="target"/>; the cached snapshot is shown meanwhile.</summary>
    private void Refresh(PaletteSource target)
    {
        var generation = ++refreshGeneration;
        Cancel(ref refreshCts);
        statusProblem = string.Empty;
        switch (target)
        {
            case PaletteSource.Files:
                files = host.CachedFiles;
                isLoadingSnapshot = true;
                Rebuild();
                StartRefresh(
                    generation,
                    ct => host.RefreshFilesAsync(ct),
                    list => files = list,
                    static exception => Loc.Instance.Format(PaletteStrings.NoticeFilesFailed, exception.Message));
                break;
            case PaletteSource.Sessions:
                sessions = host.CachedSessions;
                isLoadingSnapshot = true;
                Rebuild();
                StartRefresh(
                    generation,
                    ct => host.RefreshSessionsAsync(ct),
                    list => sessions = list,
                    static exception => Loc.Instance.Format(PaletteStrings.NoticeSessionsFailed, exception.Message));
                break;
            case PaletteSource.Symbols:
                symbols = host.CachedSymbols;
                isLoadingSnapshot = true;
                Rebuild();
                StartRefresh(
                    generation,
                    ct => host.RefreshSymbolsAsync(ct),
                    snapshot => symbols = snapshot,
                    static exception =>
                    {
                        AppLogs.Editor.ZLogWarning($"palette symbols failed: {exception.Message}");
                        return Loc.Instance[PaletteStrings.NoticeSymbolsUnavailable];
                    });
                break;
            case PaletteSource.Content:
                isLoadingSnapshot = false;
                ScheduleContentSearch();
                break;
            default:
                isLoadingSnapshot = false;
                Rebuild();
                break;
        }
    }

    private void StartRefresh<T>(
        int generation,
        Func<CancellationToken, Task<T>> fetch,
        Action<T> apply,
        Func<Exception, string> describeFailure)
    {
        var cts = new CancellationTokenSource();
        refreshCts = cts;
        _ = RefreshAsync(generation, cts, fetch, apply, describeFailure);
    }

    private async Task RefreshAsync<T>(
        int generation,
        CancellationTokenSource cts,
        Func<CancellationToken, Task<T>> fetch,
        Action<T> apply,
        Func<Exception, string> describeFailure)
    {
        try
        {
            var value = await fetch(cts.Token).ConfigureAwait(true);
            if (IsCurrent(generation, cts))
            {
                apply(value);
                statusProblem = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (IsCurrent(generation, cts))
            {
                statusProblem = describeFailure(exception);
            }
        }
        finally
        {
            if (ReferenceEquals(refreshCts, cts))
            {
                refreshCts = null;
            }

            cts.Dispose();
        }

        if (generation != refreshGeneration)
        {
            return;
        }

        isLoadingSnapshot = false;
        Rebuild(keepSelection: true);
    }

    /// <summary>Starts the debounced content search for the current query, cancelling the previous one.</summary>
    private void ScheduleContentSearch()
    {
        Cancel(ref contentCts);
        if (effectiveQuery.Length == 0)
        {
            contentResult = GrepResult.Empty;
            contentQuery = string.Empty;
            contentProblem = string.Empty;
            isTruncated = false;
            IsSearching = false;
            Rebuild();
            return;
        }

        var cts = new CancellationTokenSource();
        contentCts = cts;
        IsSearching = true;
        Rebuild();
        _ = RunContentSearchAsync(effectiveQuery, cts);
    }

    private async Task RunContentSearchAsync(string query, CancellationTokenSource cts)
    {
        var completed = false;
        try
        {
            await Task.Delay(ContentDebounceMilliseconds, cts.Token).ConfigureAwait(true);
            var result = await host.SearchContentAsync(query, cts.Token).ConfigureAwait(true);
            if (!cts.IsCancellationRequested)
            {
                contentResult = result;
                contentQuery = query;
                contentProblem = string.Empty;
                isTruncated = result.Truncated;
                completed = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (!cts.IsCancellationRequested)
            {
                contentResult = GrepResult.Empty;
                contentQuery = query;
                contentProblem = Loc.Instance.Format(PaletteStrings.NoticeSearchFailed, exception.Message);
                isTruncated = false;
                completed = true;
            }
        }
        finally
        {
            var current = ReferenceEquals(contentCts, cts);
            if (current)
            {
                contentCts = null;
                IsSearching = false;
            }

            cts.Dispose();
            if (completed && current)
            {
                Rebuild();
            }
        }
    }

    private bool IsCurrent(int generation, CancellationTokenSource cts) =>
        generation == refreshGeneration && !cts.IsCancellationRequested;

    /// <summary>The failures a search may report: what a missing directory, a hostile file or a stopped server throws.</summary>
    private static bool IsRecoverable(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
            TimeoutException or NotSupportedException;
}
