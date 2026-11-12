using System.Globalization;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.Core.Providers;

namespace Forge.App.ViewModels;

/// <summary>
/// One session's readout: the composer strip above the input. Left of the strip it answers "what is this session doing"
/// (running state, working directory, branch, ahead/behind, CI, how full the context window is); right of it, the buttons
/// that answer "what is it allowed to use and what has it used" — sub-agents, approvals, skills, MCP servers and tools —
/// plus the project the session belongs to.
/// <para>Everything here is derived, never authoritative. Usage is the root agent's, because that is what the context
/// window measures and what the selected model prices; a sub-agent's own spend stays on its tab. A source that knows
/// nothing leaves the readout blank instead of inventing a percentage or a price.</para>
/// <para>Threading: UI thread only. <see cref="RefreshCounters"/> is synchronous and cheap (tool list, MCP states, the
/// approval queue); <see cref="RefreshSources"/> adds the two reads that touch a database and the file system, and applies
/// their results on the UI thread. A stale answer is dropped by its token, and the skill scan runs at most once per session
/// because skills do not change while a session is open.</para>
/// </summary>
/// <summary>One slanted segment of the context gauge: filled up to the rounded fraction, hollow after it.</summary>
internal sealed record ContextSegment(bool IsFilled)
{
    /// <summary>How many segments the gauge has; the view lays out exactly this many.</summary>
    public const int Count = 5;

    public bool IsEmpty => !IsFilled;
}

/// <summary>
/// One session's readout: the composer strip above the input.
/// <para>Left of the strip it answers "what is this session doing" (branch, ahead/behind, CI, the four counters and the
/// workspace button); the far right carries the token reading — <c>RC</c> cache-read hit rate, the five-segment gauge and
/// <c>2%/1.0M</c>.</para>
/// </summary>
internal sealed class SessionInstrumentsViewModel : ObservableObject, IDisposable
{
    private readonly SessionInfo session;
    private readonly ISessionInstrumentSources sources;
    private readonly Func<IReadOnlyList<string>> availableTools;
    private readonly Func<string> currentModel;
    private readonly Func<string?> currentProvider;
    private readonly HashSet<string> usedTools = new(StringComparer.Ordinal);
    private readonly HashSet<string> usedSkills = new(StringComparer.OrdinalIgnoreCase);
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    private IReadOnlyList<SlashEntry> skills = [];
    private IReadOnlyList<string> tools = [];
    private TokenUsage usage = TokenUsage.Empty;
    private int lastPromptTokens;
    private int approvalRecords;
    private (int Ready, int Configured) mcp;
    private string aheadBehind = string.Empty;
    private string projectName = string.Empty;
    private string? projectRoot;
    private string latestDraft = string.Empty;
    private bool skillsLoaded;
    private bool skillsLoading;
    private int refreshToken;
    private bool disposed;

    public SessionInstrumentsViewModel(
        SessionInfo session,
        ISessionInstrumentSources sources,
        Func<IReadOnlyList<string>> availableTools,
        Func<string> currentModel,
        Func<string?>? currentProvider = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        this.session = session;
        this.sources = sources ?? EmptySessionInstrumentSources.Instance;
        this.availableTools = availableTools ?? (static () => []);
        this.currentModel = currentModel ?? (static () => string.Empty);
        this.currentProvider = currentProvider ?? (static () => null);

        OpenApprovalsCommand = new RelayCommand(() => this.sources.OpenSettingsSection(Preferences.SettingsSectionIds.Approvals));
        OpenSkillsCommand = new RelayCommand(() => this.sources.OpenSettingsSection(Preferences.SettingsSectionIds.Skills));
        OpenMcpCommand = new RelayCommand(() => this.sources.OpenSettingsSection(Preferences.SettingsSectionIds.Mcp));
        OpenToolsCommand = new RelayCommand(() => this.sources.OpenSettingsSection(Preferences.SettingsSectionIds.Tools));
        OpenProjectInVsCodeCommand = new RelayCommand(
            () => OpenInVsCode(projectRoot),
            () => CanOpenProjectInVsCode);
        OpenWorkspaceInVsCodeCommand = new RelayCommand(
            () => OpenInVsCode(WorkspacePath),
            () => CanOpenWorkspaceInVsCode);
        OpenProjectInExplorerCommand = new RelayCommand(
            () => OpenInExplorer(projectRoot),
            () => CanOpenProjectInExplorer);
        OpenWorkspaceInExplorerCommand = new RelayCommand(
            () => OpenInExplorer(WorkspacePath),
            () => CanOpenWorkspaceInExplorer);

        Loc.Instance.LanguageChanged += OnLanguageChanged;

        // Opening a tab reads the two cheap things only: the in-memory counters and the approvals record count (one
        // read-only query). The workspace's skills are scanned when a turn starts or when the settings page shows them.
        RefreshCounters();
        _ = RefreshApprovalsAsync(++refreshToken);
    }

    // ---------------------------------------------------------------- workspace and project

    /// <summary>The session's working directory: for a repository session its worktree, otherwise the scratch folder.</summary>
    public string WorkspacePath => session.WorkspacePath;

    /// <summary>
    /// The last path segment, which is what distinguishes two sessions of one repository; the strip shows this instead of
    /// the full path, and the whole path stays in the tooltip.
    /// </summary>
    public string WorkspaceName
    {
        get
        {
            var path = session.WorkspacePath;
            var trimmed = path.TrimEnd('\\', '/');
            var cut = trimmed.LastIndexOfAny(['\\', '/']);
            return cut >= 0 && cut < trimmed.Length - 1 ? trimmed[(cut + 1)..] : trimmed;
        }
    }

    public string WorkspaceTooltip => InstrumentsStrings.WorkspaceTooltip(session.WorkspacePath);

    public string ProjectName => projectName;

    public bool HasProject => projectName.Length > 0;

    /// <summary>The menu item is only offered when a VS Code shim was resolved, so it can never be a dead entry.</summary>
    public bool CanOpenWorkspaceInVsCode => sources.HasVsCode;

    public bool CanOpenProjectInVsCode => projectRoot is not null && sources.HasVsCode;

    public bool CanOpenWorkspaceInExplorer => true;

    public bool CanOpenProjectInExplorer => projectRoot is not null;

    public RelayCommand OpenProjectInVsCodeCommand { get; }

    public RelayCommand OpenWorkspaceInVsCodeCommand { get; }

    public RelayCommand OpenProjectInExplorerCommand { get; }

    public RelayCommand OpenWorkspaceInExplorerCommand { get; }

    /// <summary>
    /// The project the session belongs to. The window pushes it, because the project registry is read in the background and
    /// a session opened from a restored tab can exist before that read lands.
    /// </summary>
    public void SetProject(string? name, string? root)
    {
        var nextName = name ?? string.Empty;
        var nextRoot = string.IsNullOrWhiteSpace(root) ? null : root;
        if (string.Equals(projectName, nextName, StringComparison.Ordinal) && string.Equals(projectRoot, nextRoot, StringComparison.Ordinal))
        {
            return;
        }

        projectName = nextName;
        projectRoot = nextRoot;
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(HasProject));
        RaiseOpenTargets();
    }

    /// <summary>Opens a folder in VS Code; a missing target or shim simply does nothing.</summary>
    private void OpenInVsCode(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            sources.OpenInVsCode(path);
        }
    }

    private void OpenInExplorer(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            sources.OpenInExplorer(path);
        }
    }

    // ---------------------------------------------------------------- left half: git and the session's own state

    public string AheadBehindText => aheadBehind;

    public bool HasAheadBehind => aheadBehind.Length > 0;

    /// <summary>
    /// The ahead/behind readout of the active session, pushed by the files panel's git row rather than read: only that row
    /// knows the divergence, and the strip shows the marker, not the branch. The pull-request CI marker that used to sit
    /// next to it went away with the Git panel — waiting for checks is an agent-tool flow now.
    /// </summary>
    public void SetGit(string? aheadBehindText)
    {
        aheadBehind = aheadBehindText ?? string.Empty;
        OnPropertyChanged(nameof(AheadBehindText));
        OnPropertyChanged(nameof(HasAheadBehind));
    }

    // ---------------------------------------------------------------- usage

    public bool HasUsage => usage.InputTokens > 0 || usage.OutputTokens > 0;

    /// <summary>True when the model's context window is known <em>and</em> a turn reported a prompt size.</summary>
    public bool HasContextWindow => Metadata.ContextWindow is > 0 && lastPromptTokens > 0;

    /// <summary>The context window's fill, 0-100; 0 when the window or the prompt size is unknown.</summary>
    public double ContextPercent => SessionUsageMath.ContextRatio(lastPromptTokens, Metadata.ContextWindow) is { } ratio
        ? Math.Round(ratio * 100, 0)
        : 0;

    /// <summary>
    /// The fixed-width gauge next to the readout: five identical slanted segments, the filled ones counting up to the rounded
    /// fraction. Fixed because it is the strip's stable anchor — the numbers beside it change, the gauge does not resize.
    /// </summary>
    public IReadOnlyList<ContextSegment> ContextSegments { get; private set; } = BuildSegments(0);

    /// <summary>"RC 98.5": the cache-read hit rate of the session, one decimal, 0.0 before any usage.</summary>
    public string CacheReadText => InstrumentsStrings.CacheRead(
        (SessionUsageMath.CacheHitRatio(usage) * 100).ToString("0.0", CultureInfo.InvariantCulture));

    private static ContextSegment[] BuildSegments(double ratio)
    {
        var filled = (int)Math.Round(Math.Clamp(ratio, 0, 1) * ContextSegment.Count, MidpointRounding.AwayFromZero);
        var segments = new ContextSegment[ContextSegment.Count];
        for (var index = 0; index < segments.Length; index++)
        {
            segments[index] = new ContextSegment(index < filled);
        }

        return segments;
    }

    /// <summary>
    /// "9% / 1.0M" next to the bar. It always says something: with no usage yet, or no window for the model, the bar reads
    /// 0% rather than disappearing, so the right end of the strip keeps its shape.
    /// </summary>
    public string ContextLabel
    {
        get
        {
            var window = Metadata.ContextWindow;
            var percent = (Math.Round((SessionUsageMath.ContextRatio(lastPromptTokens, window) ?? 0) * 100, 0))
                .ToString("0", CultureInfo.InvariantCulture) + "%";
            return window is > 0 ? InstrumentsStrings.UsageContext(percent, Scale(window.Value)) : percent;
        }
    }

    /// <summary>
    /// The hover line: every number the strip's readout is made of, always all of them and always in the same order, so the
    /// tooltip never collapses to "no usage yet" and never changes shape between a fresh session and a running one. A part the
    /// model cannot report (no window, no price) reads zero.
    /// </summary>
    public string UsageTooltip
    {
        get
        {
            var metadata = Metadata;
            var cost = SessionUsageMath.Cost(usage, metadata.Pricing) ?? 0;
            return string.Join(
                " · ",
                InstrumentsStrings.UsageTokens(Scale(usage.InputTokens), Scale(usage.OutputTokens)),
                InstrumentsStrings.UsageCacheRead(Scale(usage.CacheReadTokens)),
                InstrumentsStrings.UsageCacheWrite(Scale(usage.CacheWriteTokens)),
                InstrumentsStrings.UsageCache(
                    (SessionUsageMath.CacheHitRatio(usage) * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"),
                Price(cost),
                // The window is the one model constant rather than a session number, so it sits last.
                InstrumentsStrings.UsageWindow(Scale(metadata.ContextWindow ?? 0)));
        }
    }

    private ModelMetadata Metadata => sources.ModelMetadata(currentModel(), currentProvider());

    /// <summary>
    /// One turn's delta, from the same <see cref="AgentTurnCompleted"/> arithmetic the turn card uses. Only the root agent
    /// counts: the context bar follows the conversation's own prompt, and the cost is priced with the model the composer's
    /// picker shows. A sub-agent's spend stays on its tab.
    /// </summary>
    public void AddTurn(string agentId, TokenUsage delta)
    {
        if (!string.Equals(agentId, "root", StringComparison.Ordinal))
        {
            return;
        }

        usage += delta;
        lastPromptTokens = Math.Max(0, delta.InputTokens);
        ContextSegments = BuildSegments(SessionUsageMath.ContextRatio(lastPromptTokens, Metadata.ContextWindow) ?? 0);
        OnPropertyChanged(nameof(HasUsage));
        OnPropertyChanged(nameof(HasContextWindow));
        OnPropertyChanged(nameof(ContextPercent));
        OnPropertyChanged(nameof(ContextSegments));
        OnPropertyChanged(nameof(CacheReadText));
        OnPropertyChanged(nameof(ContextLabel));
        OnPropertyChanged(nameof(UsageTooltip));
    }

    /// <summary>
    /// A manual compaction shrank the root context: the bar shows the compactor's estimate of what is left until the next
    /// turn reports a real prompt size again. Spend is untouched.
    /// </summary>
    public void NoteCompacted(int contextTokens)
    {
        lastPromptTokens = Math.Max(0, contextTokens);
        ContextSegments = BuildSegments(SessionUsageMath.ContextRatio(lastPromptTokens, Metadata.ContextWindow) ?? 0);
        OnPropertyChanged(nameof(HasContextWindow));
        OnPropertyChanged(nameof(ContextPercent));
        OnPropertyChanged(nameof(ContextSegments));
        OnPropertyChanged(nameof(ContextLabel));
    }

    /// <summary>
    /// The usage a reopened session already spent, summed from its stored messages. The prompt size of its last request is
    /// not recoverable from that sum, so the context bar stays empty until the next turn reports one.
    /// </summary>
    public void SeedHistory(TokenUsage total)
    {
        if (total.InputTokens <= 0 && total.OutputTokens <= 0)
        {
            return;
        }

        usage += total;
        OnPropertyChanged(nameof(HasUsage));
        OnPropertyChanged(nameof(UsageTooltip));
        OnPropertyChanged(nameof(CacheReadText));
    }

    // ---------------------------------------------------------------- right half: the counters

    public string ApprovalText => InstrumentsStrings.CountPair(PendingApprovals, approvalRecords);

    public string ApprovalTooltip => InstrumentsStrings.ApprovalsTooltip(PendingApprovals, approvalRecords);

    public string SkillText => InstrumentsStrings.CountPair(usedSkills.Count, skills.Count);

    public string SkillTooltip => InstrumentsStrings.SkillsTooltip(usedSkills.Count, skills.Count);

    public string McpText => InstrumentsStrings.CountPair(mcp.Ready, mcp.Configured);

    public string McpTooltip => InstrumentsStrings.McpTooltip(mcp.Ready, mcp.Configured);

    /// <summary>
    /// "11/21": tools called over tools available. Before the first turn has built the agent host the denominator is still
    /// zero and the readout says <c>0/0</c> — a number the strip can always show, rather than a question mark.
    /// </summary>
    public string ToolText => InstrumentsStrings.CountPair(usedTools.Count, tools.Count);

    public string ToolTooltip => InstrumentsStrings.ToolsTooltip(usedTools.Count, tools.Count);

    /// <summary>Counters shown as labels; when the strip is too narrow they are replaced by one overflow button.</summary>
    public string ApprovalsLabel => InstrumentsStrings.ApprovalsLabel;

    public string SkillsLabel => InstrumentsStrings.SkillsLabel;

    public string McpLabel => InstrumentsStrings.McpLabel;

    public string ToolsLabel => InstrumentsStrings.ToolsLabel;

    /// <summary>The label of the one button that carries all four counters.</summary>
    public string ResourcesLabel => InstrumentsStrings.Resources;

    public string ResourcesTooltip => InstrumentsStrings.ResourcesTooltip;

    /// <summary>One overflow-menu row per counter: the same label and pair the button shows, as plain text.</summary>
    public string ApprovalMenuText => InstrumentsStrings.MenuEntry(ApprovalsLabel, ApprovalText);

    public string SkillMenuText => InstrumentsStrings.MenuEntry(SkillsLabel, SkillText);

    public string McpMenuText => InstrumentsStrings.MenuEntry(McpLabel, McpText);

    public string ToolMenuText => InstrumentsStrings.MenuEntry(ToolsLabel, ToolText);

    public RelayCommand OpenApprovalsCommand { get; }

    public RelayCommand OpenSkillsCommand { get; }

    public RelayCommand OpenMcpCommand { get; }

    public RelayCommand OpenToolsCommand { get; }

    /// <summary>Records one tool call of this session (root or child): the numerator of the tool counter.</summary>
    public void NoteToolUsed(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName) || !usedTools.Add(toolName))
        {
            return;
        }

        OnPropertyChanged(nameof(ToolText));
        OnPropertyChanged(nameof(ToolTooltip));
    }

    /// <summary>
    /// Records the skills a sent draft references (<c>/name</c>, word-bounded), matched against what was discovered for this
    /// workspace. A skill is prose the agent then follows, so this is the only place that can see one being picked. The draft
    /// is kept, because the first turn can be sent before the discovery has landed.
    /// </summary>
    public void NoteDraft(string? draft)
    {
        if (string.IsNullOrEmpty(draft))
        {
            return;
        }

        latestDraft = draft;
        MatchDraftSkills();
    }

    /// <summary>
    /// Re-reads the cheap counters: the session's tool list, the MCP states and the approval queue. Synchronous by design —
    /// it runs on every queue change and on every turn start, so it must not touch a database or the file system.
    /// </summary>
    public void RefreshCounters()
    {
        if (disposed)
        {
            return;
        }

        tools = availableTools() ?? [];
        mcp = sources.McpServers();
        RaiseCounters();
    }

    /// <summary>
    /// Re-reads what has to be read off the UI thread: the approvals database and the workspace's skills. The skill scan runs
    /// once per session (skills do not change while a session is open); the approval count is re-read every time because the
    /// approval queue is exactly what changes behind the session's back.
    /// </summary>
    public void RefreshSources()
    {
        if (disposed)
        {
            return;
        }

        RefreshCounters();
        var token = ++refreshToken;
        var workspace = session.WorkspacePath;
        _ = RefreshSourcesAsync(token, workspace);
    }

    private async Task RefreshSourcesAsync(int token, string workspace)
    {
        await RefreshApprovalsAsync(token).ConfigureAwait(true);
        await LoadSkillsOnceAsync(workspace).ConfigureAwait(true);
    }

    /// <summary>One read-only count against the approvals database; a locked or corrupt one reads as "unknown", not zero.</summary>
    private async Task RefreshApprovalsAsync(int token)
    {
        int? records;
        try
        {
            records = await sources.ApprovalRecordsAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or InvalidDataException
            or Microsoft.Data.Sqlite.SqliteException)
        {
            records = null;
        }

        if (disposed || token != refreshToken)
        {
            return;
        }

        approvalRecords = records ?? 0;
        RaiseCounters();
    }

    private async Task LoadSkillsOnceAsync(string workspace)
    {
        if (skillsLoaded || skillsLoading)
        {
            return;
        }

        skillsLoading = true;
        IReadOnlyList<SlashEntry> discovered = [];
        try
        {
            discovered = await BackgroundRead
                .RunAsync(_ => Task.FromResult(sources.Skills(workspace)), CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No skills is a valid state; the counter then reads "0/0" instead of the strip coming apart over a directory.
        }

        skillsLoading = false;
        if (disposed)
        {
            return;
        }

        skillsLoaded = true;
        skills = discovered;
        RaiseCounters();

        // A draft sent before the scan finished still counts against the skills it mentions.
        MatchDraftSkills();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private int PendingApprovals => sources.PendingApprovals(session.Id);

    /// <summary>Re-reads what the four "open …" menu items can do; the project arrives after the strip already exists.</summary>
    private void RaiseOpenTargets()
    {
        OnPropertyChanged(nameof(CanOpenWorkspaceInVsCode));
        OnPropertyChanged(nameof(CanOpenProjectInVsCode));
        OnPropertyChanged(nameof(CanOpenWorkspaceInExplorer));
        OnPropertyChanged(nameof(CanOpenProjectInExplorer));
        OpenProjectInVsCodeCommand.NotifyCanExecuteChanged();
        OpenWorkspaceInVsCodeCommand.NotifyCanExecuteChanged();
        OpenProjectInExplorerCommand.NotifyCanExecuteChanged();
        OpenWorkspaceInExplorerCommand.NotifyCanExecuteChanged();
    }

    private void MatchDraftSkills()
    {
        if (skills.Count == 0 || latestDraft.Length == 0)
        {
            return;
        }

        var changed = false;
        foreach (var skill in skills)
        {
            if (usedSkills.Contains(skill.Name) || !MentionsSkill(latestDraft, skill.Name))
            {
                continue;
            }

            usedSkills.Add(skill.Name);
            changed = true;
        }

        if (changed)
        {
            OnPropertyChanged(nameof(SkillText));
            OnPropertyChanged(nameof(SkillTooltip));
        }
    }

    private void RaiseCounters()
    {
        OnPropertyChanged(nameof(ApprovalText));
        OnPropertyChanged(nameof(ApprovalTooltip));
        OnPropertyChanged(nameof(SkillText));
        OnPropertyChanged(nameof(SkillTooltip));
        OnPropertyChanged(nameof(McpText));
        OnPropertyChanged(nameof(McpTooltip));
        OnPropertyChanged(nameof(ToolText));
        OnPropertyChanged(nameof(ToolTooltip));
        OnPropertyChanged(nameof(ApprovalMenuText));
        OnPropertyChanged(nameof(SkillMenuText));
        OnPropertyChanged(nameof(McpMenuText));
        OnPropertyChanged(nameof(ToolMenuText));
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // A notification from another thread belongs to a window that is gone (a test host); re-reading here would touch
        // bound controls off their thread.
        if (Environment.CurrentManagedThreadId != uiThread || disposed)
        {
            return;
        }

        RaiseCounters();
        OnPropertyChanged(nameof(ContextLabel));
        OnPropertyChanged(nameof(UsageTooltip));
        OnPropertyChanged(nameof(WorkspaceTooltip));
        OnPropertyChanged(nameof(ApprovalsLabel));
        OnPropertyChanged(nameof(SkillsLabel));
        OnPropertyChanged(nameof(McpLabel));
        OnPropertyChanged(nameof(ToolsLabel));
        OnPropertyChanged(nameof(ResourcesLabel));
        OnPropertyChanged(nameof(ResourcesTooltip));
        OnPropertyChanged(nameof(ApprovalMenuText));
        OnPropertyChanged(nameof(SkillMenuText));
        OnPropertyChanged(nameof(McpMenuText));
        OnPropertyChanged(nameof(ToolMenuText));
    }

    private static bool MentionsSkill(string draft, string name)
    {
        var needle = "/" + name;
        var index = draft.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var after = index + needle.Length;
            if (after >= draft.Length || char.IsWhiteSpace(draft[after]))
            {
                return true;
            }

            index = draft.IndexOf(needle, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>Token counts the way a strip reads them: <c>900</c>, <c>36k</c>, <c>1.0M</c>.</summary>
    private static string Scale(int value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("0", CultureInfo.InvariantCulture),
    };

    /// <summary>A cost that stays readable at both ends: <c>$0.0041</c>, <c>$0.041</c>, <c>$1.23</c>.</summary>
    private static string Price(double value) => value switch
    {
        >= 10 => "$" + value.ToString("0.0", CultureInfo.InvariantCulture),
        >= 1 => "$" + value.ToString("0.00", CultureInfo.InvariantCulture),
        >= 0.01 => "$" + value.ToString("0.000", CultureInfo.InvariantCulture),
        _ => "$" + value.ToString("0.0000", CultureInfo.InvariantCulture),
    };
}
