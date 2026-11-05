using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Core.Tools;

namespace Forge.App.Approval;

/// <summary>Everything a tool call card shows; captured from the agent loop's <see cref="ApprovalRequest"/> plus the session.</summary>
internal sealed record ToolApprovalCardRequest(
    string SessionId,
    string SessionTitle,
    string? ProjectId,
    string ToolName,
    ToolSideEffect SideEffect,
    string WorkingDirectory,
    string ArgumentsJson);

/// <summary>
/// One tool call waiting for the user: session, tool name, side effect, working directory and the arguments as indented
/// JSON (formatted on a pool thread, collapsed when long). The buttons allow once, deny, or allow and remember the
/// decision for this session / this project / always; <c>extension_delete</c> has no "remember" buttons because that card
/// is the deletion confirmation.
/// </summary>
internal sealed class ToolApprovalCardViewModel : ApprovalCardViewModel
{
    /// <summary>Arguments longer than this (or taller than <see cref="LongArgumentsLines"/>) start collapsed.</summary>
    private const int LongArgumentsChars = 1_500;

    private const int LongArgumentsLines = 24;

    /// <summary>Hard cap so a pathological argument cannot build an unbounded text block.</summary>
    private const int MaxArgumentsChars = 200_000;

    private readonly ToolApprovalCardRequest request;
    private readonly IApprovalDispatcher dispatcher;
    private string argumentsText = string.Empty;
    private bool isArgumentsLong;
    private bool isArgumentsExpanded;

    public ToolApprovalCardViewModel(ToolApprovalCardRequest request, IApprovalDispatcher dispatcher)
        : base("tool:" + Guid.NewGuid().ToString("N"), request.SessionId, request.SessionTitle, DateTimeOffset.UtcNow)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.request = request;
        this.dispatcher = dispatcher ?? UiThreadDispatcher.Instance;

        AllowCommand = new RelayCommand(() => Decide(ApprovalCardDecision.Allow, remember: null), () => !IsDecided);
        DenyCommand = new RelayCommand(() => Decide(ApprovalCardDecision.Deny, remember: null), () => !IsDecided);
        RememberSessionCommand = new RelayCommand(
            () => Decide(ApprovalCardDecision.Allow, ApprovalScope.Session), () => CanRemember && !IsDecided);
        RememberProjectCommand = new RelayCommand(
            () => Decide(ApprovalCardDecision.Allow, ApprovalScope.Project), () => CanRememberProject && !IsDecided);
        RememberGlobalCommand = new RelayCommand(
            () => Decide(ApprovalCardDecision.Allow, ApprovalScope.Global), () => CanRemember && !IsDecided);
        ToggleArgumentsCommand = new RelayCommand(ToggleArguments);

        AttachLanguageChanged(OnLanguageChanged);
        _ = FormatArgumentsAsync();
    }

    public string ToolName => request.ToolName;

    public string WorkingDirectory => request.WorkingDirectory;

    /// <summary>Side effect of the call in the current language.</summary>
    public string SideEffectText => ApprovalText.SideEffect(request.SideEffect);

    /// <summary>True for the extension deletion confirmation, which replaces the generic tool-call heading.</summary>
    public bool IsDelete => !ApprovalRules.IsRememberable(request.ToolName);

    public string Title => Loc.Instance[IsDelete ? ExtensionsStrings.ToolDeleteTitle : ExtensionsStrings.ToolTitle];

    public string Note => IsDelete ? Loc.Instance[ExtensionsStrings.ToolDeleteNote] : string.Empty;

    public bool HasNote => IsDelete;

    /// <summary>The indented JSON, empty until the background formatting finished.</summary>
    public string ArgumentsText => argumentsText;

    public bool HasArguments => argumentsText.Length > 0;

    /// <summary>The arguments pane is hidden while a long argument is folded.</summary>
    public bool IsArgumentsVisible => HasArguments && (!isArgumentsLong || isArgumentsExpanded);

    public bool IsArgumentsLong => isArgumentsLong;

    public bool IsArgumentsExpanded
    {
        get => isArgumentsExpanded;
        private set
        {
            if (Set(ref isArgumentsExpanded, value))
            {
                OnPropertyChanged(nameof(ArgumentsToggleText));
                OnPropertyChanged(nameof(IsArgumentsVisible));
            }
        }
    }

    public string ArgumentsToggleText =>
        Loc.Instance[IsArgumentsExpanded ? ExtensionsStrings.ToolArgumentsCollapse : ExtensionsStrings.ToolArgumentsExpand];

    /// <summary>Remembered decisions are impossible for the deletion confirmation.</summary>
    public bool CanRemember => ApprovalRules.IsRememberable(request.ToolName);

    /// <summary>A project rule needs a project id; when the session has none the button is disabled instead of guessing.</summary>
    public bool CanRememberProject => CanRemember && request.ProjectId is { Length: > 0 };

    /// <summary>Explains the disabled project button.</summary>
    public string RememberProjectHint => CanRememberProject ? string.Empty : Loc.Instance[ExtensionsStrings.ToolNoProject];

    public RelayCommand AllowCommand { get; }

    public RelayCommand DenyCommand { get; }

    public RelayCommand RememberSessionCommand { get; }

    public RelayCommand RememberProjectCommand { get; }

    public RelayCommand RememberGlobalCommand { get; }

    public RelayCommand ToggleArgumentsCommand { get; }

    /// <summary>
    /// Indents a JSON argument so a tool call is readable on the card. A value that is not JSON is shown as it arrived;
    /// the only other transformation is the hard length cap.
    /// </summary>
    public static string FormatArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        string text;
        try
        {
            using var document = JsonDocument.Parse(json);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                document.WriteTo(writer);
            }

            text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            text = json;
        }

        return text.Length <= MaxArgumentsChars ? text : text[..MaxArgumentsChars] + "…";
    }

    private async Task FormatArgumentsAsync()
    {
        var formatted = await Task.Run(() => FormatArguments(request.ArgumentsJson)).ConfigureAwait(false);
        try
        {
            await dispatcher
                .InvokeAsync(
                    () =>
                    {
                        argumentsText = formatted;
                        isArgumentsLong = formatted.Length > LongArgumentsChars ||
                                          CountLines(formatted) > LongArgumentsLines;
                        OnPropertyChanged(nameof(ArgumentsText));
                        OnPropertyChanged(nameof(HasArguments));
                        OnPropertyChanged(nameof(IsArgumentsLong));
                        IsArgumentsExpanded = !isArgumentsLong;
                        OnPropertyChanged(nameof(IsArgumentsVisible));
                    })
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The dispatcher went away while the card was being formatted (application shutdown): nothing to show.
            Trace.WriteLine("[approval] the arguments could not be shown: " + exception.Message);
        }
    }

    private static int CountLines(string text)
    {
        var lines = 1;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                lines++;
            }
        }

        return lines;
    }

    private void ToggleArguments() => IsArgumentsExpanded = !IsArgumentsExpanded;

    private void Decide(ApprovalCardDecision decision, ApprovalScope? remember)
    {
        if (IsDecided)
        {
            return;
        }

        if (remember is { } scope && !CanRememberScope(scope))
        {
            return;
        }

        TryComplete(new ApprovalCardOutcome(decision, remember));
    }

    private bool CanRememberScope(ApprovalScope scope) =>
        CanRemember && (scope != ApprovalScope.Project || CanRememberProject);

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SideEffectText));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Note));
        OnPropertyChanged(nameof(RememberProjectHint));
        OnPropertyChanged(nameof(ArgumentsToggleText));
    }
}
