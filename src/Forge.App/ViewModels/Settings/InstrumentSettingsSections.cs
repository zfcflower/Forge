using System.Collections.ObjectModel;
using System.Diagnostics;
using Forge.App.Approval;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.Approval;

namespace Forge.App.ViewModels.Settings;

/// <summary>One read-only row of an instrument section: a heading, a secondary line and a trailing value.</summary>
internal sealed record InstrumentRow(string Title, string Detail, string Trailing)
{
    public bool HasDetail => Detail.Length > 0;

    public bool HasTrailing => Trailing.Length > 0;
}

/// <summary>
/// The shape the four instrument sections share: a hint line, rows replaced whole when a read finishes, and the absence of
/// anything editable. Reads go to the thread pool through <see cref="BackgroundRead"/> and land back on the UI thread, so
/// opening the settings page never stalls on a database or a directory scan.
/// </summary>
internal abstract class InstrumentSettingsSectionViewModel : ObservableObject, IDisposable, Preferences.IReloadableSettingsSection
{
    private readonly int uiThread = Environment.CurrentManagedThreadId;
    private string emptyText = string.Empty;
    private bool disposed;

    protected InstrumentSettingsSectionViewModel()
    {
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<InstrumentRow> Rows { get; } = [];

    public string Hint => HintText;

    public string EmptyText
    {
        get => emptyText;
        protected set => Set(ref emptyText, value);
    }

    public bool HasRows => Rows.Count > 0;

    protected abstract string HintText { get; }

    /// <summary>Re-reads the source; called when the section is first shown and after a language switch.</summary>
    public abstract void Reload();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    protected void Replace(IEnumerable<InstrumentRow> rows, string empty)
    {
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        EmptyText = empty;
        OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>Guards against a read that lands after the window closed, and against reporting off the UI thread.</summary>
    protected bool CanApply => !disposed && Environment.CurrentManagedThreadId == uiThread;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a page whose window is gone (a test host).
        if (!CanApply)
        {
            return;
        }

        OnPropertyChanged(nameof(Hint));
        Reload();
    }
}

/// <summary>The <c>approvals</c> section: every plugin version the approval engine has a record for.</summary>
internal sealed class ApprovalsSettingsSectionViewModel(AppComposition composition) : InstrumentSettingsSectionViewModel
{
    protected override string HintText => InstrumentsStrings.ApprovalsSectionHint;

    public override void Reload()
    {
        var databasePath = composition.ApprovalsDatabasePath;
        _ = LoadAsync(databasePath);
    }

    private async Task LoadAsync(string? databasePath)
    {
        IReadOnlyList<PluginRecord> records;
        var unavailable = false;
        try
        {
            records = string.IsNullOrWhiteSpace(databasePath)
                ? []
                : await BackgroundRead
                    .RunAsync(token => ApprovalStore.ListReadOnlyAsync(databasePath, token), CancellationToken.None)
                    .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            Trace.WriteLine("[instruments] approvals list failed: " + exception.Message);
            records = [];
            unavailable = true;
        }

        if (!CanApply)
        {
            return;
        }

        Replace(
            records.Select(static record => new InstrumentRow(
                record.PluginId + " · " + record.Version,
                record.Status.ToString(),
                TextFormat.ShortStamp(record.UpdatedAt))),
            unavailable ? InstrumentsStrings.ApprovalsSectionUnavailable : InstrumentsStrings.ApprovalsSectionEmpty);
    }
}

/// <summary>The <c>skills</c> section: what the composer's <c>/</c> picker would find, with the file it came from.</summary>
internal sealed class SkillsSettingsSectionViewModel(AppComposition composition) : InstrumentSettingsSectionViewModel
{
    protected override string HintText => InstrumentsStrings.SkillsSectionHint;

    public override void Reload()
    {
        var workspace = composition.WorkspaceDirectory;
        _ = LoadAsync(workspace);
    }

    private async Task LoadAsync(string workspace)
    {
        IReadOnlyList<SlashEntry> skills;
        try
        {
            skills = await BackgroundRead
                .RunAsync(
                    _ => Task.FromResult(SkillCatalog.Discover(workspace, Agents.AgentRuntimeServices.UserDirectory())),
                    CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.WriteLine("[instruments] skills list failed: " + exception.Message);
            skills = [];
        }

        if (!CanApply)
        {
            return;
        }

        Replace(
            skills.Select(static skill => new InstrumentRow(skill.Name, skill.Description, skill.Path)),
            InstrumentsStrings.SkillsSectionEmpty);
    }
}

/// <summary>The <c>mcp</c> section: the servers in <c>mcp.json</c> and what the manager reports for each.</summary>
internal sealed class McpSettingsSectionViewModel(AppComposition composition) : InstrumentSettingsSectionViewModel
{
    protected override string HintText => InstrumentsStrings.McpSectionHint;

    public override void Reload()
    {
        var states = composition.Mcp?.States ?? [];
        Replace(
            states.Select(static state => new InstrumentRow(
                state.Name,
                state.Enabled ? InstrumentsStrings.McpSectionEnabled : InstrumentsStrings.McpSectionDisabled,
                state.LastError is { Length: > 0 } error
                    ? state.Status.ToString() + " · " + Shorten(error)
                    : state.Status.ToString())),
            InstrumentsStrings.McpSectionEmpty);
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..79] + "…";
}
