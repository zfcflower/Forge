using System.Collections.ObjectModel;
using System.Diagnostics;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Core.Prompts.Templates;

namespace Forge.App.Settings.Prompts;

/// <summary>What the editor pane is showing: a read-only built-in, one of the user's files, or a template not yet created.</summary>
internal enum PromptEditorMode
{
    BuiltIn,
    Custom,
    New,
}

/// <summary>One entry of the kind combo box (the combo shows <see cref="ToString"/>).</summary>
internal sealed record PromptKindChoice(string Label, PromptTemplateKind Value)
{
    public override string ToString() => Label;
}

/// <summary>One entry of the scope combo box (the combo shows <see cref="ToString"/>).</summary>
internal sealed record PromptScopeChoice(string Label, PromptTemplateScope Value)
{
    public override string ToString() => Label;
}

/// <summary>
/// The right pane of the prompts section: the full text of one template (frontmatter plus body) with live validation and
/// the actions that fit its mode. All store calls run on the thread pool through the owner; results are applied on the
/// UI thread (the awaiting continuation). It captures the workspace it was opened for, so a save always targets the file it
/// read even if the active project changes meanwhile.
/// </summary>
internal sealed class PromptEditorViewModel : NotifyingObject
{
    private const string CommandSkeleton = "---\ndescription: \n---\n";
    private const string AgentSkeleton = "---\nname: \ndescription: \n---\n";

    private readonly PromptsSettingsViewModel owner;
    private readonly string? workspace;
    private readonly PromptRowViewModel? row;
    private string text;
    private string original;
    private string? revision;
    private string? path;
    private string id;
    private PromptKindChoice kind;
    private PromptScopeChoice scope;
    private string? status;
    private bool statusIsError;
    private bool confirmingDelete;
    private bool busy;
    private int validationVersion;

    private PromptEditorViewModel(
        PromptsSettingsViewModel owner,
        PromptEditorMode mode,
        PromptRowViewModel? row,
        PromptTemplateText? stored,
        string? workspace)
    {
        this.owner = owner;
        this.row = row;
        this.workspace = workspace;
        Mode = mode;
        text = stored?.Text ?? CommandSkeleton;
        original = text;
        revision = stored?.Revision;
        path = stored?.Path;
        id = row?.Id ?? string.Empty;

        KindChoices = BuildKindChoices();
        ScopeChoices = BuildScopeChoices(workspace is not null);
        kind = KindChoices.FirstOrDefault(choice => choice.Value == (row?.Kind ?? PromptTemplateKind.Command)) ?? KindChoices[0];
        scope = ScopeChoices.FirstOrDefault(choice => choice.Value == (row?.Scope ?? PromptTemplateScope.Global)) ?? ScopeChoices[0];

        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(), () => CanSave);
        RevertCommand = new AsyncRelayCommand(_ => RevertAsync(), () => CanRevert);
        DeleteCommand = new RelayCommand(AskDelete, () => IsCustom);
        ConfirmDeleteCommand = new AsyncRelayCommand(_ => DeleteAsync(), () => ConfirmingDelete);
        CancelDeleteCommand = new RelayCommand(() => ConfirmingDelete = false);
        CopyGlobalCommand = new AsyncRelayCommand(_ => CopyAsync(PromptTemplateScope.Global), () => IsBuiltIn);
        CopyProjectCommand = new AsyncRelayCommand(_ => CopyAsync(PromptTemplateScope.Project), () => IsBuiltIn && HasWorkspace);
    }

    public PromptEditorMode Mode { get; }

    public bool IsBuiltIn => Mode == PromptEditorMode.BuiltIn;

    public bool IsCustom => Mode == PromptEditorMode.Custom;

    public bool IsNew => Mode == PromptEditorMode.New;

    public bool IsReadOnly => IsBuiltIn;

    public bool HasWorkspace => workspace is not null;

    /// <summary>The address of the template being edited; null while creating a new one.</summary>
    public PromptTemplateRef? Ref => row?.Ref;

    /// <summary>The stored revision the next save is checked against; null for a built-in or a template not yet created.</summary>
    public string? Revision => revision;

    public IReadOnlyList<PromptKindChoice> KindChoices { get; }

    public IReadOnlyList<PromptScopeChoice> ScopeChoices { get; }

    public ObservableCollection<string> Problems { get; } = [];

    public bool HasProblems => Problems.Count > 0;

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand RevertCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public AsyncRelayCommand ConfirmDeleteCommand { get; }

    public RelayCommand CancelDeleteCommand { get; }

    public AsyncRelayCommand CopyGlobalCommand { get; }

    public AsyncRelayCommand CopyProjectCommand { get; }

    public string Title => IsNew ? Loc.Instance[PromptsStrings.NewKey] : row!.DisplayName;

    public string Description => row?.Description ?? string.Empty;

    public bool HasDescription => Description.Length > 0;

    public string IdLabel => Loc.Instance[PromptsStrings.IdLabelKey];

    public string KindLabel => Loc.Instance[PromptsStrings.KindLabelKey];

    public string ScopeLabel => Loc.Instance[PromptsStrings.ScopeLabelKey];

    public string FileLabel => Loc.Instance[PromptsStrings.FileLabelKey];

    public string TextLabel => Loc.Instance[PromptsStrings.TextLabelKey];

    public string ProblemsTitle => Loc.Instance[PromptsStrings.ProblemsTitleKey];

    public string AppendHint => Loc.Instance[PromptsStrings.AppendHintKey];

    public string BuiltInHint => Loc.Instance[PromptsStrings.BuiltInReadOnlyKey];

    public string NoWorkspaceHint => Loc.Instance[PromptsStrings.NoWorkspaceKey];

    public bool ShowsNoWorkspaceHint => !HasWorkspace && !IsCustom;

    public string SaveText => Loc.Instance[IsNew ? PromptsStrings.CreateKey : PromptsStrings.SaveKey];

    public string RevertText => Loc.Instance[PromptsStrings.RevertKey];

    public string DeleteText => Loc.Instance[PromptsStrings.DeleteKey];

    public string DeleteConfirmText => Loc.Instance[PromptsStrings.DeleteConfirmKey];

    public string DeleteCancelText => Loc.Instance[PromptsStrings.DeleteCancelKey];

    public string CopyGlobalText => Loc.Instance[PromptsStrings.CopyGlobalKey];

    public string CopyProjectText => Loc.Instance[PromptsStrings.CopyProjectKey];

    /// <summary>The kind as a fixed label (existing entries); the new-template form uses <see cref="SelectedKind"/> instead.</summary>
    public string KindText => row is null ? string.Empty : PromptRowViewModel.KindName(row.Kind);

    public string ScopeText => row is null ? string.Empty : PromptRowViewModel.ScopeName(row.Scope);

    /// <summary>The file the entry lives in; empty for a built-in and for a template not yet created.</summary>
    public string PathText => path ?? string.Empty;

    public bool HasPath => !string.IsNullOrEmpty(path);

    public string Id
    {
        get => id;
        set
        {
            if (!IsNew)
            {
                return;
            }

            if (Set(ref id, value ?? string.Empty))
            {
                ScheduleValidation();
            }
        }
    }

    public PromptKindChoice SelectedKind
    {
        get => kind;
        set
        {
            if (value is null || !IsNew || !Set(ref kind, value))
            {
                return;
            }

            // Swap the skeleton only while the user has not typed anything of their own.
            if (!IsDirty || text is CommandSkeleton or AgentSkeleton)
            {
                Text = value.Value == PromptTemplateKind.Agent ? AgentSkeleton : CommandSkeleton;
                original = text;
                OnPropertyChanged(nameof(IsDirty));
            }

            ScheduleValidation();
        }
    }

    public PromptScopeChoice SelectedScope
    {
        get => scope;
        set
        {
            if (value is not null && IsNew)
            {
                Set(ref scope, value);
            }
        }
    }

    public string Text
    {
        get => text;
        set
        {
            if (IsReadOnly)
            {
                return;
            }

            if (Set(ref text, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(IsDirty));
                NotifyCommands();
                ScheduleValidation();
            }
        }
    }

    public bool IsDirty => !string.Equals(text, original, StringComparison.Ordinal);

    public string? Status
    {
        get => status;
        private set => SetAndNotify(ref status, value, nameof(HasStatus));
    }

    public bool HasStatus => !string.IsNullOrEmpty(status);

    public bool StatusIsError
    {
        get => statusIsError;
        private set => Set(ref statusIsError, value);
    }

    /// <summary>True between asking to delete and confirming or cancelling it.</summary>
    public bool ConfirmingDelete
    {
        get => confirmingDelete;
        private set
        {
            if (Set(ref confirmingDelete, value))
            {
                OnPropertyChanged(nameof(ShowsDeleteButton));
                ConfirmDeleteCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowsDeleteButton => IsCustom && !ConfirmingDelete;

    private bool CanSave => !IsBuiltIn && (IsNew || IsDirty);

    private bool CanRevert => IsCustom ? IsDirty : IsNew && IsDirty;

    internal static PromptEditorViewModel ForBuiltIn(PromptsSettingsViewModel owner, PromptRowViewModel row, PromptTemplateText stored, string? workspace) =>
        new(owner, PromptEditorMode.BuiltIn, row, stored, workspace);

    internal static PromptEditorViewModel ForCustom(PromptsSettingsViewModel owner, PromptRowViewModel row, PromptTemplateText stored, string? workspace) =>
        new(owner, PromptEditorMode.Custom, row, stored, workspace);

    internal static PromptEditorViewModel ForNew(PromptsSettingsViewModel owner, string? workspace)
    {
        var editor = new PromptEditorViewModel(owner, PromptEditorMode.New, null, null, workspace);
        editor.ScheduleValidation();
        return editor;
    }

    /// <summary>Re-reads every localized text after a language switch.</summary>
    internal void RefreshLanguage()
    {
        OnPropertyChanged(null);
        Problems.Clear();
        _ = ValidateAsync();
    }

    /// <summary>Validates the current text now (the live path debounces and then calls this) and replaces <see cref="Problems"/>.</summary>
    internal async Task ValidateAsync()
    {
        if (IsBuiltIn)
        {
            SetProblems([]);
            return;
        }

        var version = ++validationVersion;
        var targetKind = IsNew ? kind.Value : row!.Ref.Kind;
        var targetId = IsNew ? (id.Length == 0 ? "new" : id) : row!.Id;
        var current = text;
        IReadOnlyList<PromptTemplateProblem> found;
        try
        {
            found = await owner.RunAsync(() => owner.Service.Store.Validate(targetKind, targetId, current)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.WriteLine("[prompts] validate failed: " + exception.Message);
            return;
        }

        if (version == validationVersion)
        {
            SetProblems(found);
        }
    }

    internal async Task SaveAsync()
    {
        if (IsBuiltIn || !Begin())
        {
            return;
        }

        try
        {
            if (IsNew)
            {
                await CreateAsync().ConfigureAwait(true);
                return;
            }

            var target = row!.Ref;
            var current = text;
            var expected = revision;
            PromptTemplateSaveResult result;
            try
            {
                result = await owner.RunAsync(() => owner.Service.Save(target, workspace, current, expected)).ConfigureAwait(true);
            }
            catch (Exception exception) when (IsIo(exception))
            {
                Fail(exception);
                return;
            }

            switch (result.Status)
            {
                case PromptTemplateSaveStatus.Saved:
                    revision = result.Revision;
                    original = current;
                    OnPropertyChanged(nameof(IsDirty));
                    NotifyCommands();
                    SetStatus(Loc.Instance[PromptsStrings.MsgSavedKey], error: false);
                    await ValidateAsync().ConfigureAwait(true);
                    await owner.RefreshAsync().ConfigureAwait(true);
                    break;
                case PromptTemplateSaveStatus.Conflict:
                    SetStatus(Loc.Instance[PromptsStrings.MsgConflictKey], error: true);
                    break;
                case PromptTemplateSaveStatus.Invalid:
                    SetProblems(result.Problems);
                    SetStatus(Loc.Instance[PromptsStrings.MsgInvalidKey], error: true);
                    break;
                default:
                    SetStatus(Loc.Instance[PromptsStrings.MsgReadOnlyKey], error: true);
                    break;
            }
        }
        finally
        {
            End();
        }
    }

    /// <summary>Custom: reads the file again (also the way out of a save conflict). New: back to the skeleton.</summary>
    internal async Task RevertAsync()
    {
        if (IsBuiltIn || !Begin())
        {
            return;
        }

        try
        {
            if (IsNew)
            {
                text = kind.Value == PromptTemplateKind.Agent ? AgentSkeleton : CommandSkeleton;
                original = text;
                OnPropertyChanged(nameof(Text));
                OnPropertyChanged(nameof(IsDirty));
                NotifyCommands();
                SetStatus(null, error: false);
                ScheduleValidation();
                return;
            }

            var target = row!.Ref;
            PromptTemplateText? stored;
            try
            {
                stored = await owner.RunAsync(() => owner.Service.Store.Read(target, workspace)).ConfigureAwait(true);
            }
            catch (Exception exception) when (IsIo(exception))
            {
                Fail(exception);
                return;
            }

            if (stored is null)
            {
                SetStatus(Loc.Instance[PromptsStrings.MsgGoneKey], error: true);
                await owner.RefreshAsync().ConfigureAwait(true);
                return;
            }

            text = stored.Text;
            original = stored.Text;
            revision = stored.Revision;
            path = stored.Path;
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(PathText));
            OnPropertyChanged(nameof(HasPath));
            NotifyCommands();
            SetStatus(null, error: false);
            await ValidateAsync().ConfigureAwait(true);
        }
        finally
        {
            End();
        }
    }

    internal async Task DeleteAsync()
    {
        if (!IsCustom || !Begin())
        {
            return;
        }

        try
        {
            ConfirmingDelete = false;
            var target = row!.Ref;
            var expected = revision;
            if (expected is null)
            {
                SetStatus(Loc.Instance[PromptsStrings.MsgGoneKey], error: true);
                return;
            }

            PromptTemplateSaveStatus result;
            try
            {
                result = await owner.RunAsync(() => owner.Service.Delete(target, workspace, expected)).ConfigureAwait(true);
            }
            catch (Exception exception) when (IsIo(exception))
            {
                Fail(exception);
                return;
            }

            switch (result)
            {
                case PromptTemplateSaveStatus.Saved:
                    await owner.AfterDeleteAsync(target, Loc.Instance[PromptsStrings.MsgDeletedKey]).ConfigureAwait(true);
                    break;
                case PromptTemplateSaveStatus.Conflict:
                    SetStatus(Loc.Instance[PromptsStrings.MsgConflictKey], error: true);
                    break;
                default:
                    SetStatus(Loc.Instance[PromptsStrings.MsgReadOnlyKey], error: true);
                    break;
            }
        }
        finally
        {
            End();
        }
    }

    /// <summary>Writes the built-in's text as a file in the chosen scope and opens it for editing; that file now shadows the built-in.</summary>
    internal async Task CopyAsync(PromptTemplateScope target)
    {
        if (!IsBuiltIn || !Begin())
        {
            return;
        }

        try
        {
            if (target == PromptTemplateScope.Project && workspace is null)
            {
                SetStatus(Loc.Instance[PromptsStrings.NoWorkspaceKey], error: true);
                return;
            }

            var reference = new PromptTemplateRef(row!.Ref.Kind, target, row.Id);
            var current = text;
            PromptTemplateSaveResult result;
            try
            {
                result = await owner.RunAsync(() => owner.Service.Save(reference, workspace, current, null)).ConfigureAwait(true);
            }
            catch (Exception exception) when (IsIo(exception))
            {
                Fail(exception);
                return;
            }

            switch (result.Status)
            {
                case PromptTemplateSaveStatus.Saved:
                    await owner.OpenAfterCreateAsync(reference, Loc.Instance[PromptsStrings.MsgCopiedKey]).ConfigureAwait(true);
                    break;
                case PromptTemplateSaveStatus.Conflict:
                    SetStatus(Loc.Instance[PromptsStrings.MsgExistsKey], error: true);
                    break;
                case PromptTemplateSaveStatus.Invalid:
                    SetProblems(result.Problems);
                    SetStatus(Loc.Instance[PromptsStrings.MsgInvalidKey], error: true);
                    break;
                default:
                    SetStatus(Loc.Instance[PromptsStrings.MsgReadOnlyKey], error: true);
                    break;
            }
        }
        finally
        {
            End();
        }
    }

    private async Task CreateAsync()
    {
        if (kind.Value == PromptTemplateKind.Slot)
        {
            SetStatus(Loc.Instance[PromptsStrings.MsgSlotNewKey], error: true);
            return;
        }

        if (!PromptTemplateIds.IsValid(id))
        {
            SetStatus(Loc.Instance[PromptsStrings.MsgInvalidIdKey], error: true);
            return;
        }

        if (scope.Value == PromptTemplateScope.Project && workspace is null)
        {
            SetStatus(Loc.Instance[PromptsStrings.NoWorkspaceKey], error: true);
            return;
        }

        var reference = new PromptTemplateRef(kind.Value, scope.Value, id);
        if (owner.Exists(reference))
        {
            SetStatus(Loc.Instance[PromptsStrings.MsgExistsKey], error: true);
            return;
        }

        var current = text;
        PromptTemplateSaveResult result;
        try
        {
            result = await owner.RunAsync(() => owner.Service.Save(reference, workspace, current, null)).ConfigureAwait(true);
        }
        catch (Exception exception) when (IsIo(exception))
        {
            Fail(exception);
            return;
        }

        switch (result.Status)
        {
            case PromptTemplateSaveStatus.Saved:
                await owner.OpenAfterCreateAsync(reference, Loc.Instance[PromptsStrings.MsgSavedKey]).ConfigureAwait(true);
                break;
            case PromptTemplateSaveStatus.Conflict:
                SetStatus(Loc.Instance[PromptsStrings.MsgExistsKey], error: true);
                break;
            case PromptTemplateSaveStatus.Invalid:
                SetProblems(result.Problems);
                SetStatus(Loc.Instance[PromptsStrings.MsgInvalidKey], error: true);
                break;
            default:
                SetStatus(Loc.Instance[PromptsStrings.MsgReadOnlyKey], error: true);
                break;
        }
    }

    /// <summary>Shows a message on a freshly opened editor (after a copy, create or delete).</summary>
    internal void ShowStatus(string message) => SetStatus(message, error: false);

    internal void ShowWarning(string message) => SetStatus(message, error: true);

    private void AskDelete()
    {
        if (IsCustom)
        {
            ConfirmingDelete = true;
        }
    }

    private void ScheduleValidation()
    {
        if (IsBuiltIn)
        {
            return;
        }

        _ = DebouncedValidateAsync(++validationVersion);
    }

    private async Task DebouncedValidateAsync(int version)
    {
        try
        {
            if (owner.ValidationDelay > TimeSpan.Zero)
            {
                await Task.Delay(owner.ValidationDelay).ConfigureAwait(true);
            }

            if (version == validationVersion)
            {
                await ValidateAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.WriteLine("[prompts] validation failed: " + exception.Message);
        }
    }

    private void SetProblems(IReadOnlyList<PromptTemplateProblem> problems)
    {
        Problems.Clear();
        foreach (var problem in problems)
        {
            Problems.Add(PromptsStrings.Problem(problem));
        }

        OnPropertyChanged(nameof(HasProblems));
    }

    private void SetStatus(string? message, bool error)
    {
        Status = message;
        StatusIsError = error;
    }

    private void Fail(Exception exception) =>
        SetStatus(Loc.Instance.Format(PromptsStrings.MsgFailedKey, exception.Message), error: true);

    private static bool IsIo(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;

    private bool Begin()
    {
        if (busy)
        {
            return false;
        }

        busy = true;
        return true;
    }

    private void End() => busy = false;

    private void NotifyCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    private static PromptKindChoice[] BuildKindChoices() =>
    [
        new(PromptRowViewModel.KindName(PromptTemplateKind.Command), PromptTemplateKind.Command),
        new(PromptRowViewModel.KindName(PromptTemplateKind.Agent), PromptTemplateKind.Agent),
    ];

    private static PromptScopeChoice[] BuildScopeChoices(bool hasWorkspace) => hasWorkspace
        ?
        [
            new(PromptRowViewModel.ScopeName(PromptTemplateScope.Global), PromptTemplateScope.Global),
            new(PromptRowViewModel.ScopeName(PromptTemplateScope.Project), PromptTemplateScope.Project),
        ]
        :
        [
            new(PromptRowViewModel.ScopeName(PromptTemplateScope.Global), PromptTemplateScope.Global),
        ];
}
