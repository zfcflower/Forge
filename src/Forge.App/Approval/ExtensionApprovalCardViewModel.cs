using System.Diagnostics;
using System.Globalization;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Editor.Diff;
using Forge.Plugin.Sdk;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Approval;

/// <summary>
/// One document of a staged extension as the approval card shows it: a new file is shown in full, a changed file as a
/// per-file line diff against the live version, a file the draft did not touch is listed with an "unchanged" badge. The
/// diff runs on a pool thread; the rows are a flat list so the card virtualizes them.
/// </summary>
internal sealed class ExtensionFileViewModel : ObservableObject
{
    private readonly bool isNew;
    private readonly bool isChanged;

    private ExtensionFileViewModel(string path, string text, IReadOnlyList<ExtensionDiffRowViewModel> rows, bool isNew, bool isChanged)
    {
        Path = path;
        FullText = text;
        Rows = new RangeObservableCollection<ExtensionDiffRowViewModel>();
        Rows.ReplaceAll(rows);
        this.isNew = isNew;
        this.isChanged = isChanged;
    }

    public string Path { get; }

    /// <summary>The staged text; the template shows it for a new file.</summary>
    public string FullText { get; }

    /// <summary>Diff rows of a changed file; empty for a new or unchanged one.</summary>
    public RangeObservableCollection<ExtensionDiffRowViewModel> Rows { get; }

    public bool IsNew => isNew;

    public bool IsUnchanged => !isNew && !isChanged;

    public bool IsChanged => isChanged;

    public bool HasRows => Rows.Count > 0;

    public string Badge => Loc.Instance[isNew
        ? ExtensionsStrings.ExtensionFileAdded
        : isChanged
            ? ExtensionsStrings.ExtensionFileChanged
            : ExtensionsStrings.ExtensionFileUnchanged];

    public string AddedLines => "+" + Rows.Count(static row => row.IsAdd).ToString(CultureInfo.InvariantCulture);

    public string RemovedLines => "−" + Rows.Count(static row => row.IsRemove).ToString(CultureInfo.InvariantCulture);

    /// <summary>Builds one file's rows from the staged text and, for an update, the text of the live version.</summary>
    public static ExtensionFileViewModel Create(StagedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var isNew = file.PreviousText is null;
        var isChanged = isNew || !string.Equals(file.PreviousText, file.Text, StringComparison.Ordinal);
        var rows = new List<ExtensionDiffRowViewModel>();
        if (isChanged && !isNew)
        {
            var previousLines = SplitLines(file.PreviousText!);
            var nextLines = SplitLines(file.Text);
            var diff = LineDiff.Compute(previousLines, nextLines);
            foreach (var hunk in diff.Hunks)
            {
                rows.Add(ExtensionDiffRowViewModel.Hunk(
                    "@@ -" + (hunk.OldStart + 1).ToString(CultureInfo.InvariantCulture) + "," +
                    hunk.OldCount.ToString(CultureInfo.InvariantCulture) + " +" +
                    (hunk.NewStart + 1).ToString(CultureInfo.InvariantCulture) + "," +
                    hunk.NewCount.ToString(CultureInfo.InvariantCulture) + " @@"));
                foreach (var line in hunk.Lines)
                {
                    rows.Add(ExtensionDiffRowViewModel.Line(
                        line.Kind,
                        line.OldLine,
                        line.NewLine,
                        line.OldLine >= 0 && line.OldLine < previousLines.Count ? previousLines[line.OldLine] : string.Empty,
                        line.NewLine >= 0 && line.NewLine < nextLines.Count ? nextLines[line.NewLine] : string.Empty));
                }
            }
        }

        return new ExtensionFileViewModel(file.Path, file.Text, rows, isNew, isChanged);
    }

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>Re-reads the language-dependent badge after a switch.</summary>
    internal void RaiseText() => OnPropertyChanged(nameof(Badge));
}

/// <summary>One row of a file diff on the plugin card: a hunk header, an added, a removed or a context line.</summary>
internal sealed class ExtensionDiffRowViewModel
{
    private ExtensionDiffRowViewModel(string prefix, string oldNumber, string newNumber, string text, bool isAdd, bool isRemove, bool isHunk)
    {
        Prefix = prefix;
        OldNumber = oldNumber;
        NewNumber = newNumber;
        Text = text;
        IsAdd = isAdd;
        IsRemove = isRemove;
        IsHunk = isHunk;
    }

    public string Prefix { get; }

    public string OldNumber { get; }

    public string NewNumber { get; }

    public string Text { get; }

    public bool IsAdd { get; }

    public bool IsRemove { get; }

    public bool IsHunk { get; }

    public static ExtensionDiffRowViewModel Hunk(string header) =>
        new(string.Empty, string.Empty, string.Empty, header, isAdd: false, isRemove: false, isHunk: true);

    public static ExtensionDiffRowViewModel Line(DiffLineKind kind, int oldLine, int newLine, string oldText, string newText) =>
        kind switch
        {
            DiffLineKind.Insert => new ExtensionDiffRowViewModel(
                "+",
                string.Empty,
                Number(newLine),
                newText,
                isAdd: true,
                isRemove: false,
                isHunk: false),
            DiffLineKind.Delete => new ExtensionDiffRowViewModel(
                "-",
                Number(oldLine),
                string.Empty,
                oldText,
                isAdd: false,
                isRemove: true,
                isHunk: false),
            _ => new ExtensionDiffRowViewModel(
                " ",
                Number(oldLine),
                Number(newLine),
                newText,
                isAdd: false,
                isRemove: false,
                isHunk: false),
        };

    private static string Number(int line) => line >= 0 ? (line + 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
}

/// <summary>
/// A staged extension waiting for the user: the full text of a new extension or the per-file diff of an update, the
/// manifest permissions and dependencies, the three hashes <c>extensions.commit</c> must echo back, and the compile
/// diagnostics. Only approve and reject - an extension can never be "remembered".
/// </summary>
internal sealed class ExtensionApprovalCardViewModel : ApprovalCardViewModel
{
    private readonly StagedExtension staged;
    private readonly IApprovalDispatcher dispatcher;
    private readonly bool fromPreviousRun;
    private readonly RangeObservableCollection<ExtensionFileViewModel> files = [];
    private bool diffBuilt;

    public ExtensionApprovalCardViewModel(
        StagedExtension staged,
        string sessionId,
        string sessionTitle,
        bool fromPreviousRun,
        IApprovalDispatcher? dispatcher = null)
        : base("extension:" + staged.PluginId, sessionId, sessionTitle, staged.StagedAt ?? DateTimeOffset.UtcNow)
    {
        ArgumentNullException.ThrowIfNull(staged);
        this.staged = staged;
        this.dispatcher = dispatcher ?? UiThreadDispatcher.Instance;
        this.fromPreviousRun = fromPreviousRun;

        ApproveCommand = new RelayCommand(Approve, () => CanApprove && !IsDecided);
        RejectCommand = new RelayCommand(Reject, () => !IsDecided);

        AttachLanguageChanged(OnLanguageChanged);
        _ = BuildDiffAsync();
    }

    protected override ApprovalCardOutcome WithdrawalOutcome => new(ApprovalCardDecision.Withdrawn);

    public string PluginId => staged.PluginId;

    public string Version => staged.Version;

    public string LiveVersion => staged.LiveVersion ?? string.Empty;

    public bool IsUpdate => staged.IsUpdate;

    public bool HasLiveVersion => staged.LiveVersion is { Length: > 0 };

    public bool FromPreviousRun => fromPreviousRun;

    public string Title => Loc.Instance[(IsUpdate, fromPreviousRun) switch
    {
        (true, true) => ExtensionsStrings.ExtensionTitleUpdatePreviousRun,
        (true, false) => ExtensionsStrings.ExtensionTitleUpdate,
        (false, true) => ExtensionsStrings.ExtensionTitleNewPreviousRun,
        _ => ExtensionsStrings.ExtensionTitleNew,
    }];

    public string KindText => ApprovalText.Kind(staged.Kind);

    /// <summary>Whether the card can name the session it came from; a card restored after a restart cannot.</summary>
    public bool HasSession => !string.IsNullOrWhiteSpace(SessionId);

    public string PermissionsText => staged.Permissions.Count == 0
        ? Loc.Instance[ExtensionsStrings.ExtensionNone]
        : string.Join(", ", staged.Permissions.Select(PermissionText));

    public string DependenciesText => staged.Dependencies.Count == 0
        ? Loc.Instance[ExtensionsStrings.ExtensionNone]
        : string.Join(", ", staged.Dependencies.Select(static dependency => dependency.Name + " " + dependency.Version));

    /// <summary>Whether the draft declares the one tool a workflow exposes; the user must see what the model will be able to call.</summary>
    public bool HasTool => staged.Tool is not null;

    public string ToolName => staged.Tool?.Name ?? string.Empty;

    public string ToolDescription => staged.Tool is { } tool && !string.IsNullOrWhiteSpace(tool.Description)
        ? tool.Description
        : Loc.Instance[ExtensionsStrings.ExtensionToolNoDescription];

    /// <summary>The declared input schema (JSON text) as the author wrote it.</summary>
    public string ToolSchema => staged.Tool?.InputSchemaJson ?? string.Empty;

    public string SourceHash => staged.SourceHash;

    public string DependencyHash => staged.DependencyHash;

    public string SdkVersion => staged.SdkVersion;

    /// <summary>The diff rows, empty until the pool thread finished comparing the staged files with the live version.</summary>
    public RangeObservableCollection<ExtensionFileViewModel> Files => files;

    public bool HasFiles => files.Count > 0;

    public bool IsDiffReady => diffBuilt;

    /// <summary>True when every staged file is byte-identical to the live version (an update that changes nothing).</summary>
    public bool HasNoFileChanges => diffBuilt && files.Count > 0 && files.All(static file => file.IsUnchanged);

    public IReadOnlyList<string> Diagnostics => staged.Diagnostics;

    /// <summary>The diagnostics as one block of text; a draft with any is never committed from the card.</summary>
    public string DiagnosticsText => string.Join(Environment.NewLine, staged.Diagnostics);

    public bool HasDiagnostics => staged.Diagnostics.Count > 0;

    /// <summary>A staged draft with diagnostics cannot be committed, so approving is not offered.</summary>
    public bool CanApprove => !HasDiagnostics;

    /// <summary>The hashes the commit must echo back, exactly as the card shows them.</summary>
    public StagedExtension Staged => staged;

    public RelayCommand ApproveCommand { get; }

    public RelayCommand RejectCommand { get; }

    /// <summary>Builds every file's rows off the UI thread; public so the formatting rules are unit-testable.</summary>
    public static IReadOnlyList<ExtensionFileViewModel> BuildFiles(StagedExtension staged)
    {
        ArgumentNullException.ThrowIfNull(staged);
        var built = new List<ExtensionFileViewModel>(staged.Files.Count);
        foreach (var file in staged.Files)
        {
            built.Add(ExtensionFileViewModel.Create(file));
        }

        built.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return built;
    }

    private static string PermissionText(Permission permission) =>
        Loc.Instance[permission.Kind switch
        {
            PermissionKind.ReadFile => ExtensionsStrings.PermissionReadFile,
            PermissionKind.WriteFile => ExtensionsStrings.PermissionWriteFile,
            PermissionKind.ExecuteCommand => ExtensionsStrings.PermissionExecuteCommand,
            PermissionKind.Network => ExtensionsStrings.PermissionNetwork,
            PermissionKind.Database => ExtensionsStrings.PermissionDatabase,
            _ => ExtensionsStrings.PermissionUnknown,
        }] + (string.IsNullOrWhiteSpace(permission.Scope) ? string.Empty : " · " + permission.Scope);

    private async Task BuildDiffAsync()
    {
        var built = await Task.Run(() => BuildFiles(staged)).ConfigureAwait(false);
        try
        {
            await dispatcher
                .InvokeAsync(
                    () =>
                    {
                        files.ReplaceAll(built);
                        diffBuilt = true;
                        OnPropertyChanged(nameof(Files));
                        OnPropertyChanged(nameof(HasFiles));
                        OnPropertyChanged(nameof(IsDiffReady));
                        OnPropertyChanged(nameof(HasNoFileChanges));
                    })
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The dispatcher is gone (application shutdown); the card has nobody to show itself to.
            AppLogs.Approval.ZLogWarning($"the staged files could not be shown: {exception.Message}");
        }
    }

    private void Approve()
    {
        if (!CanApprove || IsDecided)
        {
            return;
        }

        TryComplete(ApprovalCardOutcome.Allow);
    }

    private void Reject()
    {
        if (IsDecided)
        {
            return;
        }

        TryComplete(ApprovalCardOutcome.Deny);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(PermissionsText));
        OnPropertyChanged(nameof(ToolDescription));
        foreach (var file in files)
        {
            file.RaiseText();
        }
    }
}
