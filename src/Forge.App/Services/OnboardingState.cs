using Forge.Abstractions;
using Forge.App.Logging;
using Forge.App.ViewModels;
using ZLogger;

namespace Forge.App.Services;

/// <summary>
/// Whether the first-run wizard has been shown: one row in the workbench UI state (<c>ui_state</c>, the store the layout and
/// the open tabs already use), so the answer belongs to this database rather than to <c>settings.json</c> — deleting the
/// settings file to recover from a broken preference does not put a returning user through setup again.
/// <para>The row's presence is the whole record; its schema <see cref="CurrentVersion"/> leaves room for a later wizard to
/// store what was skipped. A store that cannot be read or written is not a first run and not a failure: the wizard stays
/// away and the reason is traced, so a broken database costs one wizard rather than a startup error.</para>
/// </summary>
internal sealed class OnboardingState
{
    /// <summary>The UI state key; also the unit of the on/off switch in a test.</summary>
    public const string Key = "onboarding";

    public const int CurrentVersion = 1;

    private readonly IUiStateStore uiState;

    public OnboardingState(IUiStateStore uiState)
    {
        ArgumentNullException.ThrowIfNull(uiState);
        this.uiState = uiState;
    }

    /// <summary>True when this database has already been through the wizard.</summary>
    public async Task<bool> HasCompletedAsync(CancellationToken ct)
    {
        try
        {
            // Off the dispatcher like every other ui_state access (LayoutStateLoader): this runs on the window's startup path,
            // and a SQLite read's synchronous part would otherwise stall the first frame.
            var entry = await BackgroundRead.RunAsync(token => uiState.GetAsync(Key, token), ct).ConfigureAwait(false);
            return entry is not null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLogs.Shell.ZLogWarning(exception, $"onboarding state unreadable, treating the wizard as seen");
            return true;
        }
    }

    /// <summary>Records that the wizard will not be shown again for this database.</summary>
    public async Task MarkCompletedAsync(CancellationToken ct)
    {
        try
        {
            await BackgroundRead.RunAsync(token => uiState.PutAsync(Key, CurrentVersion, "{\"completed\":true}", token), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The wizard was already closed in front of the user; failing here would reopen it on the next start, which is
            // exactly the noise the record exists to prevent, so the write is best effort and says so in the log.
            AppLogs.Shell.ZLogWarning(exception, $"onboarding state not saved");
        }
    }
}
