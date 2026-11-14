using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.Core.Tools;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Approval;

/// <summary>Everything a tool call card shows; captured from the agent loop's <see cref="ApprovalRequest"/> plus the session.</summary>
internal sealed record ToolApprovalCardRequest(
    string SessionId,
    string SessionTitle,
    string? ProjectId,
    string ToolName,
    ToolSideEffect SideEffect,
    string WorkingDirectory,
    string ArgumentsJson,
    McpApprovalServer? McpServer = null,
    string? RunKey = null);

/// <summary>
/// One tool call waiting for the user: session, tool name, side effect, working directory and the arguments as indented
/// JSON (formatted on a pool thread, collapsed when long), plus - when the tool belongs to an MCP server - the server's
/// name, transport, command line, working directory, URL and env/header key names, and a revoke button for every
/// remembered rule that names this tool. The buttons allow once, deny, or allow and remember the decision for this
/// session / this project / always; <c>extension_delete</c> and plugin commands have no "remember" buttons
/// (the first is the deletion confirmation, the second is decided per call).
/// </summary>
internal sealed class ToolApprovalCardViewModel : ApprovalCardViewModel
{
    /// <summary>Arguments longer than this (or taller than <see cref="LongArgumentsLines"/>) start collapsed.</summary>
    private const int LongArgumentsChars = 1_500;

    private const int LongArgumentsLines = 24;

    /// <summary>Hard cap so a pathological argument cannot build an unbounded text block.</summary>
    private const int MaxArgumentsChars = 200_000;

    /// <summary>Hard cap so a pathological MCP command line cannot build an unbounded text block.</summary>
    private const int MaxMcpCommandLineChars = 4_000;

    private readonly ToolApprovalCardRequest request;
    private readonly IApprovalDispatcher dispatcher;
    private readonly ApprovalRules? rules;
    private readonly McpApprovalServer? mcpServer;
    private string argumentsText = string.Empty;
    private string mcpCommandLine = string.Empty;
    private bool isArgumentsLong;
    private bool isArgumentsExpanded;

    public ToolApprovalCardViewModel(ToolApprovalCardRequest request, IApprovalDispatcher dispatcher, ApprovalRules? rules = null)
        : base("tool:" + Guid.NewGuid().ToString("N"), request.SessionId, request.SessionTitle, DateTimeOffset.UtcNow)
    {
        ArgumentNullException.ThrowIfNull(request);
        this.request = request;
        this.dispatcher = dispatcher ?? UiThreadDispatcher.Instance;
        this.rules = rules;
        mcpServer = request.McpServer;

        AllowCommand = new RelayCommand(() => Decide(ApprovalCardDecision.Allow, remember: null), () => !IsDecided);
        DenyCommand = new RelayCommand(() => Decide(ApprovalCardDecision.Deny, remember: null), () => !IsDecided);
        RememberSessionCommand = new RelayCommand(
            () => Decide(ApprovalCardDecision.Allow, ApprovalScope.Session), () => CanRemember && !IsDecided);
        RememberProjectCommand = new RelayCommand(
            () => Decide(ApprovalCardDecision.Allow, ApprovalScope.Project), () => CanRememberProject && !IsDecided);
        RememberGlobalCommand = new RelayCommand(
            () => Decide(ApprovalCardDecision.Allow, ApprovalScope.Global), () => CanRemember && !IsDecided);
        AllowRunCommand = new RelayCommand(
            () => Decide(ApprovalCardDecision.Allow, remember: null, allowRestOfRun: true), () => HasRunScope && !IsDecided);
        ToggleArgumentsCommand = new RelayCommand(ToggleArguments);
        McpRevokeCommand = new RelayCommand(RevokeMcp, () => CanRevokeRemembered && !IsDecided);

        AttachLanguageChanged(OnLanguageChanged);
        _ = FormatArgumentsAsync();
        if (mcpServer is not null)
        {
            _ = FormatMcpCommandLineAsync();
        }
    }

    public string ToolName => request.ToolName;

    public string WorkingDirectory => request.WorkingDirectory;

    /// <summary>True when the card carries what the tool's MCP server is; the whole server block binds to this.</summary>
    public bool HasMcpServer => mcpServer is not null;

    public string McpServerName => mcpServer?.ServerName ?? string.Empty;

    /// <summary>The transport in the current language; a token that is neither <c>stdio</c> nor <c>http</c> shows as it arrived.</summary>
    public string McpTransportText => mcpServer is null ? string.Empty : ExtensionsStrings.ToolMcpTransportText(mcpServer.Transport);

    public bool HasMcpCommand => mcpServer is { } server && (!string.IsNullOrEmpty(server.Command) || server.Args.Count > 0);

    /// <summary>The <c>command + args</c> line, empty until the background formatting finished.</summary>
    public string McpCommandLine => mcpCommandLine;

    public bool HasMcpWorkingDirectory => mcpServer is { WorkingDirectory.Length: > 0 };

    public string McpWorkingDirectory => mcpServer?.WorkingDirectory ?? string.Empty;

    public bool HasMcpUrl => mcpServer is { Url.Length: > 0 };

    public string McpUrl => mcpServer?.Url ?? string.Empty;

    public bool HasMcpEnvKeys => mcpServer is { EnvKeys.Count: > 0 };

    public string McpEnvKeysText => mcpServer is null ? string.Empty : string.Join(", ", mcpServer.EnvKeys);

    public bool HasMcpHeaderKeys => mcpServer is { HeaderKeys.Count: > 0 };

    public string McpHeaderKeysText => mcpServer is null ? string.Empty : string.Join(", ", mcpServer.HeaderKeys);

    /// <summary>Always true: the card shows only key names, so the hint row under them is always there.</summary>
    public bool McpHidesValues => true;

    /// <summary>True while at least one remembered rule names this card's tool; a null rule store means never.</summary>
    public bool CanRevokeRemembered => RememberedRuleCount() > 0;

    /// <summary>The revoke button text with the count of remembered rules, in the current language.</summary>
    public string RememberedRulesText
    {
        get
        {
            var count = RememberedRuleCount();
            return count == 0 ? string.Empty : ExtensionsStrings.ToolMcpRevokeRemembered(count);
        }
    }

    /// <summary>Side effect of the call in the current language.</summary>
    public string SideEffectText => ApprovalText.SideEffect(request.SideEffect);

    /// <summary>True for the extension deletion confirmation, which replaces the generic tool-call heading.</summary>
    public bool IsDelete => string.Equals(request.ToolName, ExtensionToolNames.Delete, StringComparison.Ordinal);

    /// <summary>True for a plugin command approval (<c>plugin-command:&lt;plugin&gt;:&lt;command&gt;</c>), which cannot be remembered.</summary>
    public bool IsPluginCommand => ApprovalRules.IsPluginCommand(request.ToolName);

    public string Title => Loc.Instance[
        IsDelete ? ExtensionsStrings.ToolDeleteTitle : IsPluginCommand ? ExtensionsStrings.ToolCommandTitle : ExtensionsStrings.ToolTitle];

    public string Note => IsDelete
        ? Loc.Instance[ExtensionsStrings.ToolDeleteNote]
        : IsPluginCommand ? Loc.Instance[ExtensionsStrings.ToolCommandNote] : string.Empty;

    public bool HasNote => IsDelete || IsPluginCommand;

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
            if (SetAndNotify(ref isArgumentsExpanded, value, nameof(ArgumentsToggleText)))
            {
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

    /// <summary>True when the card carries a script run's key: the call is one of a script's inner tool calls, so the
    /// "allow the rest of this run" action and the waiting count line appear.</summary>
    public bool HasRunScope => request.RunKey is not null;

    /// <summary>How many further calls of this tool the same script run has waiting behind this card; kept current by the
    /// session's approval handler while they arrive and leave. Not part of the decision - context for it.</summary>
    public int PendingCount
    {
        get => pendingCount;
        internal set
        {
            if (SetAndNotify(ref pendingCount, value, nameof(RunPendingText)))
            {
                OnPropertyChanged(nameof(HasPendingCalls));
            }
        }
    }

    private int pendingCount;

    /// <summary>Whether the waiting-count line has anything to show.</summary>
    public bool HasPendingCalls => pendingCount > 0;

    /// <summary>The waiting-count line in the current language.</summary>
    public string RunPendingText => ExtensionsStrings.ToolRunPending(pendingCount);

    public RelayCommand AllowCommand { get; }

    public RelayCommand DenyCommand { get; }

    public RelayCommand RememberSessionCommand { get; }

    public RelayCommand RememberProjectCommand { get; }

    public RelayCommand RememberGlobalCommand { get; }

    public RelayCommand AllowRunCommand { get; }

    public RelayCommand ToggleArgumentsCommand { get; }

    /// <summary>Revokes every remembered rule that names this card's tool; the card's own decision is untouched.</summary>
    public RelayCommand McpRevokeCommand { get; }

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

    /// <summary>
    /// Renders the server's command line for the card: the command and its arguments joined by spaces; an argument that
    /// contains whitespace or is empty is wrapped in double quotes. The only other transformation is the hard length cap.
    /// </summary>
    public static string FormatMcpCommandLine(McpApprovalServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(server.Command))
        {
            builder.Append(server.Command);
        }

        foreach (var arg in server.Args)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            if (arg.Length == 0 || arg.Any(static character => char.IsWhiteSpace(character)))
            {
                builder.Append('"').Append(arg).Append('"');
            }
            else
            {
                builder.Append(arg);
            }
        }

        var text = builder.ToString();
        return text.Length <= MaxMcpCommandLineChars ? text : text[..MaxMcpCommandLineChars] + "…";
    }

    private async Task FormatMcpCommandLineAsync()
    {
        var server = mcpServer;
        if (server is null)
        {
            return;
        }

        var formatted = await Task.Run(() => FormatMcpCommandLine(server)).ConfigureAwait(false);
        try
        {
            await dispatcher
                .InvokeAsync(
                    () =>
                    {
                        mcpCommandLine = formatted;
                        OnPropertyChanged(nameof(McpCommandLine));
                    })
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The dispatcher went away while the card was being formatted (application shutdown): nothing to show.
            AppLogs.Approval.ZLogWarning($"the MCP command line could not be shown: {exception.Message}");
        }
    }

    /// <summary>How many remembered rules name this card's tool, across all scopes.</summary>
    private int RememberedRuleCount()
    {
        if (rules is null)
        {
            return 0;
        }

        var count = 0;
        foreach (var rule in rules.Rules)
        {
            if (string.Equals(rule.Key.ToolName, request.ToolName, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private void RevokeMcp()
    {
        if (rules is null || IsDecided)
        {
            return;
        }

        // The snapshot is taken once: revoking a rule inside the loop never invalidates it.
        foreach (var rule in rules.Rules)
        {
            if (string.Equals(rule.Key.ToolName, request.ToolName, StringComparison.Ordinal))
            {
                rules.Revoke(rule.Key);
            }
        }

        OnPropertyChanged(nameof(CanRevokeRemembered));
        OnPropertyChanged(nameof(RememberedRulesText));
        McpRevokeCommand.NotifyCanExecuteChanged();
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
            AppLogs.Approval.ZLogWarning($"the arguments could not be shown: {exception.Message}");
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

    private void Decide(ApprovalCardDecision decision, ApprovalScope? remember, bool allowRestOfRun = false)
    {
        if (IsDecided)
        {
            return;
        }

        if (remember is { } scope && !CanRememberScope(scope))
        {
            return;
        }

        TryComplete(new ApprovalCardOutcome(decision, remember, allowRestOfRun));
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
        OnPropertyChanged(nameof(McpTransportText));
        OnPropertyChanged(nameof(RememberedRulesText));
        OnPropertyChanged(nameof(CanRevokeRemembered));
        OnPropertyChanged(nameof(RunPendingText));
    }
}
