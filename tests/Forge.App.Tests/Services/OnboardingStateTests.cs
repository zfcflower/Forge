using Forge.Abstractions;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// <see cref="OnboardingState"/> (wave W9): the record is one row in the workbench UI state, so "this database has seen the
/// first-run wizard" survives deleting <c>settings.json</c>. A store that cannot be read means the wizard stays away (a
/// broken database costs one wizard, not a startup error) and a store that cannot be written is not an error either; a
/// cancellation is still a cancellation and travels out.
/// </summary>
public sealed class OnboardingStateTests
{
    [Fact]
    public async Task A_database_without_the_row_has_not_seen_the_wizard()
    {
        var store = new RecordingUiStateStore();
        var state = new OnboardingState(store);

        Assert.False(await state.HasCompletedAsync(CancellationToken.None));
        Assert.Equal([OnboardingState.Key], store.Reads);
    }

    [Fact]
    public async Task Marking_completed_writes_the_row_and_reads_back_as_seen()
    {
        var store = new RecordingUiStateStore();
        var state = new OnboardingState(store);

        await state.MarkCompletedAsync(CancellationToken.None);

        var write = Assert.Single(store.Writes);
        Assert.Equal(OnboardingState.Key, write.Key);
        Assert.Equal(OnboardingState.CurrentVersion, write.Version);
        Assert.Contains("\"completed\":true", write.Json, StringComparison.Ordinal);
        Assert.True(await state.HasCompletedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unreadable_store_counts_as_seen()
    {
        // The database is broken or locked: the wizard must not take the startup down with it.
        var store = new RecordingUiStateStore { ReadFailure = new InvalidOperationException("the store is gone") };

        Assert.True(await new OnboardingState(store).HasCompletedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unwritable_store_does_not_throw()
    {
        // The wizard was already closed in front of the user; failing the write must not reopen it on the next start.
        var store = new RecordingUiStateStore { WriteFailure = new IOException("the disk is full") };
        var state = new OnboardingState(store);

        await state.MarkCompletedAsync(CancellationToken.None);

        Assert.Single(store.Writes);
        Assert.False(await state.HasCompletedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_cancellation_is_not_swallowed()
    {
        // The two catches exclude OperationCanceledException on purpose: a cancelled read is the caller's decision, not a
        // broken store, and reporting it as "seen" would hide the cancellation from the caller.
        var store = new RecordingUiStateStore { ReadFailure = new OperationCanceledException() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new OnboardingState(store).HasCompletedAsync(CancellationToken.None));
    }

    // ---------------------------------------------------------------- the double

    /// <summary>An in-memory <see cref="IUiStateStore"/> that can be made to fail either way and records every call.</summary>
    private sealed class RecordingUiStateStore : IUiStateStore
    {
        private readonly Dictionary<string, UiStateEntry> entries = new(StringComparer.Ordinal);

        public List<string> Reads { get; } = [];

        public List<(string Key, int Version, string Json)> Writes { get; } = [];

        public Exception? ReadFailure { get; init; }

        public Exception? WriteFailure { get; init; }

        public Task<UiStateEntry?> GetAsync(string key, CancellationToken ct)
        {
            Reads.Add(key);
            if (ReadFailure is { } failure)
            {
                return Task.FromException<UiStateEntry?>(failure);
            }

            return Task.FromResult(entries.TryGetValue(key, out var entry) ? entry : null);
        }

        public Task PutAsync(string key, int version, string json, CancellationToken ct)
        {
            Writes.Add((key, version, json));
            if (WriteFailure is { } failure)
            {
                return Task.FromException(failure);
            }

            entries[key] = new UiStateEntry(key, version, json, DateTimeOffset.UnixEpoch);
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string key, CancellationToken ct) =>
            Task.FromResult(entries.Remove(key));
    }
}