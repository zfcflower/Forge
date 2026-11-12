using System.Collections.ObjectModel;
using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Prompts;
using Forge.App.ViewModels;

namespace Forge.App.Settings.Prompts;

/// <summary>Which rows the list shows.</summary>
internal enum PromptFilter
{
    All,
    BuiltIn,
    Custom,
}

/// <summary>
/// The <c>prompts</c> settings section: a filterable list of every command, agent and built-in prompt, and the editor of the
/// selected one. It owns no window. The store does file IO, so every call goes through <see cref="RunAsync"/> (thread pool)
/// and the continuation lands back on the UI thread, where the collections are touched. A late result of a superseded
/// reload or selection is dropped by version counters.
/// </summary>
internal sealed class PromptsSettingsViewModel : NotifyingObject, IDisposable, IReloadableSettingsSection
{
    private readonly Func<string?> workspace;
    private readonly List<PromptRowViewModel> allRows = [];
    private IReadOnlyList<PromptTemplateEntry> entries = [];
    private PromptTemplateRef? selectedRef;
    private PromptEditorViewModel? editor;
    private PromptFilter filter = PromptFilter.All;
    private string? listProblem;
    private int reloadVersion;
    private int openVersion;
    private bool discardArmed;
    private PromptTemplateRef? discardTarget;
    private bool disposed;

    public PromptsSettingsViewModel(PromptTemplateService service, Func<string?> workspace, TimeSpan? validationDelay = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(workspace);
        Service = service;
        this.workspace = workspace;
        ValidationDelay = validationDelay ?? TimeSpan.FromMilliseconds(250);
        NewCommand = new RelayCommand(OpenNew);
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    internal PromptTemplateService Service { get; }

    /// <summary>The pause after the last keystroke before the editor validates its text.</summary>
    internal TimeSpan ValidationDelay { get; }

    public RelayCommand NewCommand { get; }

    /// <summary>The list as shown: commands, agents, then built-in prompts; groups with no visible row are left out.</summary>
    public ObservableCollection<PromptGroupViewModel> Groups { get; } = [];

    public bool HasRows => Groups.Count > 0;

    public bool IsEmpty => Groups.Count == 0;

    public PromptEditorViewModel? Editor
    {
        get => editor;
        private set
        {
            if (Set(ref editor, value))
            {
                OnPropertyChanged(nameof(HasEditor));
                OnPropertyChanged(nameof(ShowsSelectHint));
            }
        }
    }

    public bool HasEditor => editor is not null;

    public bool ShowsSelectHint => editor is null;

    public string? ListProblem
    {
        get => listProblem;
        private set => SetAndNotify(ref listProblem, value, nameof(HasListProblem));
    }

    public bool HasListProblem => !string.IsNullOrEmpty(listProblem);

    public PromptFilter Filter
    {
        get => filter;
        set
        {
            if (Set(ref filter, value))
            {
                OnPropertyChanged(nameof(IsFilterAll));
                OnPropertyChanged(nameof(IsFilterBuiltIn));
                OnPropertyChanged(nameof(IsFilterCustom));
                Rebuild();
            }
        }
    }

    public bool IsFilterAll
    {
        get => filter == PromptFilter.All;
        set => SetFilter(value, PromptFilter.All);
    }

    public bool IsFilterBuiltIn
    {
        get => filter == PromptFilter.BuiltIn;
        set => SetFilter(value, PromptFilter.BuiltIn);
    }

    public bool IsFilterCustom
    {
        get => filter == PromptFilter.Custom;
        set => SetFilter(value, PromptFilter.Custom);
    }

    public string Title => Loc.Instance[PromptsStrings.SectionTitleKey];

    public string Hint => Loc.Instance[PromptsStrings.HintKey];

    public string FilterAllText => Loc.Instance[PromptsStrings.FilterAllKey];

    public string FilterBuiltInText => Loc.Instance[PromptsStrings.FilterBuiltInKey];

    public string FilterCustomText => Loc.Instance[PromptsStrings.FilterCustomKey];

    public string NewText => Loc.Instance[PromptsStrings.NewKey];

    public string EmptyText => Loc.Instance[PromptsStrings.EmptyKey];

    public string SelectHint => Loc.Instance[PromptsStrings.SelectHintKey];

    /// <summary>Re-reads the listing when the section is shown; fire-and-forget (see <see cref="ReloadAsync"/>).</summary>
    public void Reload() => _ = ReloadAsync();

    /// <summary>Lists the store on the thread pool and rebuilds the rows. The open editor is kept while its entry still exists.</summary>
    public async Task ReloadAsync()
    {
        var version = ++reloadVersion;
        var root = workspace();
        IReadOnlyList<PromptTemplateEntry> found;
        string? problem = null;
        try
        {
            found = await RunAsync(() => Service.Store.List(root)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Trace.WriteLine("[prompts] list failed: " + exception.Message);
            found = [];
            problem = Loc.Instance.Format(PromptsStrings.MsgFailedKey, exception.Message);
        }

        if (disposed || version != reloadVersion)
        {
            return;
        }

        entries = found;
        ListProblem = problem;
        Rebuild();

        // The open entry vanished (deleted outside Forge): close its editor rather than save into a missing file.
        if (editor is { IsNew: false } && selectedRef is { } current && FindRow(current) is null)
        {
            selectedRef = null;
            Editor = null;
        }
    }

    /// <summary>Opens the editor of one row (reads its text on the thread pool).</summary>
    /// <remarks>
    /// With unsaved edits the first click only warns; clicking the same target again discards them. That keeps the
    /// confirmation inline like delete's, with no dialog.
    /// </remarks>
    internal Task SelectAsync(PromptRowViewModel row)
    {
        // Re-clicking the row being edited keeps the edits.
        if ((row.Ref == selectedRef && Editor is { IsDirty: true }) || !ConfirmDiscard(row.Ref))
        {
            return Task.CompletedTask;
        }

        return OpenAsync(row.Ref, null);
    }

    /// <summary>True when nothing unsaved would be lost, or this is the second request for the same target.</summary>
    private bool ConfirmDiscard(PromptTemplateRef? target)
    {
        if (Editor is not { IsDirty: true } editor || (discardArmed && discardTarget == target))
        {
            discardArmed = false;
            discardTarget = null;
            return true;
        }

        discardArmed = true;
        discardTarget = target;
        editor.ShowWarning(Loc.Instance[PromptsStrings.MsgUnsavedKey]);
        return false;
    }

    /// <summary>After a create or copy: reloads the list, then opens the new file with a message.</summary>
    internal async Task OpenAfterCreateAsync(PromptTemplateRef reference, string message)
    {
        await ReloadAsync().ConfigureAwait(true);
        await OpenAsync(reference, message).ConfigureAwait(true);
    }

    /// <summary>After a delete: reloads the list and shows the built-in that applies again, or closes the editor when there is none.</summary>
    internal async Task AfterDeleteAsync(PromptTemplateRef deleted, string message)
    {
        await ReloadAsync().ConfigureAwait(true);
        var builtIn = new PromptTemplateRef(deleted.Kind, PromptTemplateScope.BuiltIn, deleted.Id);
        if (FindRow(builtIn) is not null)
        {
            await OpenAsync(builtIn, message).ConfigureAwait(true);
        }
        else
        {
            SetSelection(null);
            Editor = null;
        }
    }

    /// <summary>Re-lists after a save (badges such as "invalid" may change) without touching the editor.</summary>
    internal Task RefreshAsync() => ReloadAsync();

    /// <summary>True when a custom file with that kind, scope and id is already listed.</summary>
    internal bool Exists(PromptTemplateRef reference) =>
        entries.Any(entry => entry.Ref == reference);

    internal Task<T> RunAsync<T>(Func<T> work) => Task.Run(work);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private async Task OpenAsync(PromptTemplateRef reference, string? message)
    {
        var version = ++openVersion;
        var root = workspace();
        var row = FindRow(reference);
        if (row is null)
        {
            return;
        }

        SetSelection(reference);
        PromptTemplateText? stored;
        try
        {
            stored = await RunAsync(() => Service.Store.Read(reference, root)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (!disposed && version == openVersion)
            {
                ListProblem = Loc.Instance.Format(PromptsStrings.MsgFailedKey, exception.Message);
            }

            return;
        }

        if (disposed || version != openVersion)
        {
            return;
        }

        if (stored is null)
        {
            // Deleted between the listing and now.
            SetSelection(null);
            Editor = null;
            ListProblem = Loc.Instance[PromptsStrings.MsgGoneKey];
            await ReloadAsync().ConfigureAwait(true);
            return;
        }

        ListProblem = null;
        var opened = row.IsBuiltIn
            ? PromptEditorViewModel.ForBuiltIn(this, row, stored, root)
            : PromptEditorViewModel.ForCustom(this, row, stored, root);
        Editor = opened;
        if (message is not null)
        {
            opened.ShowStatus(message);
        }

        if (!row.IsBuiltIn)
        {
            _ = opened.ValidateAsync();
        }
    }

    private void OpenNew()
    {
        if (!ConfirmDiscard(null))
        {
            return;
        }

        openVersion++;
        SetSelection(null);
        Editor = PromptEditorViewModel.ForNew(this, workspace());
    }

    private void SetFilter(bool value, PromptFilter target)
    {
        if (value)
        {
            Filter = target;
        }
    }

    private PromptRowViewModel? FindRow(PromptTemplateRef reference) =>
        allRows.FirstOrDefault(row => row.Ref == reference);

    private void SetSelection(PromptTemplateRef? reference)
    {
        selectedRef = reference;
        foreach (var row in allRows)
        {
            row.IsSelected = reference is not null && row.Ref == reference;
        }
    }

    /// <summary>Rebuilds row view models from the cached entries and applies the filter; keeps the selection mark.</summary>
    private void Rebuild()
    {
        allRows.Clear();
        foreach (var entry in entries)
        {
            allRows.Add(new PromptRowViewModel(this, entry) { IsSelected = selectedRef is not null && entry.Ref == selectedRef });
        }

        Groups.Clear();
        AddGroup(PromptTemplateKind.Command, PromptsStrings.GroupCommandsKey);
        AddGroup(PromptTemplateKind.Agent, PromptsStrings.GroupAgentsKey);
        AddGroup(PromptTemplateKind.Slot, PromptsStrings.GroupSlotsKey);
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void AddGroup(PromptTemplateKind kind, string titleKey)
    {
        var rows = allRows.Where(row => row.Kind == kind && Passes(row)).ToList();
        if (rows.Count > 0)
        {
            Groups.Add(new PromptGroupViewModel(kind, Loc.Instance[titleKey], rows));
        }
    }

    private bool Passes(PromptRowViewModel row) => filter switch
    {
        PromptFilter.BuiltIn => row.IsBuiltIn,
        PromptFilter.Custom => !row.IsBuiltIn,
        _ => true,
    };

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        OnPropertyChanged(null);
        Rebuild();
        editor?.RefreshLanguage();
    }
}
