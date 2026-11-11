using System.Runtime.CompilerServices;
using Forge.Abstractions;

namespace Forge.App.Localization;

/// <summary>
/// The session's table: the session view, turn cards, tool rows, notices, sub-agent tabs and the finished sub-agent list.
/// <para>Keys: <c>session.*</c>, <c>turn.*</c>, <c>subagent.*</c>. Each key has a Chinese and an English text; parameterized texts are exposed as typed accessor
/// methods (formatted through <see cref="Loc.Format"/>), parameterless ones as properties that read
/// <see cref="Loc.Instance"/>, so callers never cache a resolved string across a language switch.</para>
/// <para>Text that is stored now and shown later - a transcript notice, a status note, the placeholder a tool row shows
/// when its call never returned - is a <see cref="LocText"/>: the accessor returns the key plus its arguments and the view
/// resolves it when it renders, so a card recorded in one language reads in the next. Data that is never translated (a
/// tool name, a model's answer, an exception message) goes through <see cref="LocText.Literal"/> instead.</para>
/// <para>Numbers and times are formatted by callers with <see cref="TextFormat"/> (grouped counts, 24-hour clock, elapsed
/// time in the language of the moment) and passed in as strings.</para>
/// </summary>
internal static class SessionStrings
{
    // ---------------------------------------------------------------- session view
    public const string StartConversationKey = "session.startConversation";
    public const string ChatHintKey = "session.chatHint";
    public const string MessagePlaceholderKey = "session.messagePlaceholder";
    public const string MessageNameKey = "session.messageName";
    public const string SendKey = "session.send";
    public const string SendTooltipKey = "session.sendTooltip";
    public const string StopTurnKey = "session.stopTurn";
    public const string StopNameKey = "session.stopName";
    public const string StoppedKey = "session.stopped";
    public const string ReadFailedKey = "session.readFailed";
    public const string ProviderMissingKey = "session.providerMissing";

    // ---------------------------------------------------------------- composer
    public const string ComposerAgentKey = "session.composer.agent";
    public const string ComposerAgentDefaultKey = "session.composer.agentDefault";
    public const string ComposerAgentDefaultDescriptionKey = "session.composer.agentDefaultDescription";
    public const string ComposerAgentTooltipKey = "session.composer.agentTooltip";
    public const string ComposerModelTooltipKey = "session.composer.modelTooltip";
    public const string ComposerSearchModelsKey = "session.composer.searchModels";
    public const string ComposerNoModelsKey = "session.composer.noModels";
    public const string ComposerPinKey = "session.composer.pin";
    public const string ComposerEffortKey = "session.composer.effort";
    public const string ComposerEffortOffKey = "session.composer.effort.off";
    public const string ComposerEffortLowKey = "session.composer.effort.low";
    public const string ComposerEffortMediumKey = "session.composer.effort.medium";
    public const string ComposerEffortHighKey = "session.composer.effort.high";
    public const string ComposerSaveDefaultsKey = "session.composer.saveDefaults";
    public const string ComposerDefaultsSavedKey = "session.composer.defaultsSaved";
    public const string ComposerRefreshKey = "session.composer.refresh";
    public const string ComposerModelsFetchedKey = "session.composer.modelsFetched";
    public const string ComposerModelFetchFailedKey = "session.composer.modelFetchFailed";
    public const string ComposerRunningMenuKey = "session.composer.runningMenu";
    public const string ComposerInterjectKey = "session.composer.interject";
    public const string ComposerInterjectHintKey = "session.composer.interjectHint";
    public const string ComposerQueueKey = "session.composer.queue";
    public const string ComposerQueueHintKey = "session.composer.queueHint";
    public const string ComposerRedirectKey = "session.composer.redirect";
    public const string ComposerRedirectHintKey = "session.composer.redirectHint";
    public const string ComposerStopHintKey = "session.composer.stopHint";
    public const string ComposerQueuedKey = "session.composer.queued";
    public const string ComposerInterjectingKey = "session.composer.interjecting";
    public const string ComposerRemoveQueuedKey = "session.composer.removeQueued";
    public const string ComposerQueuePausedKey = "session.composer.queuePaused";
    public const string ComposerInterjectedNoticeKey = "session.composer.interjectedNotice";
    public const string ComposerSkillKey = "session.composer.skill";
    public const string ComposerCommandKey = "session.composer.command";
    public const string ComposerNoMatchesKey = "session.composer.noMatches";
    // ---------------------------------------------------------------- turn card and the notices around it
    public const string TurnHeaderKey = "turn.header";
    public const string TurnRunningKey = "turn.running";
    public const string TurnTokenSummaryKey = "turn.tokenSummary";
    public const string TurnCollapseKey = "turn.collapse";
    public const string ToolArgumentsKey = "turn.toolArguments";
    public const string ToolResultKey = "turn.toolResult";
    public const string NoTextKey = "turn.noText";
    public const string NoToolResultKey = "turn.noToolResult";
    public const string TurnFailedKey = "turn.failed";
    public const string DeniedKey = "turn.denied";
    public const string CompactedKey = "turn.compacted";
    public const string CheckpointFailedKey = "turn.checkpointFailed";
    public const string SteeredNoticeKey = "turn.steeredNotice";
    public const string FollowUpNoticeKey = "turn.followUpNotice";
    public const string StopReasonEndTurnKey = "turn.stopReason.endTurn";
    public const string StopReasonToolUseKey = "turn.stopReason.toolUse";
    public const string StopReasonMaxTokensKey = "turn.stopReason.maxTokens";
    public const string StopReasonUnknownKey = "turn.stopReason.unknown";

    // ---------------------------------------------------------------- sub-agents
    public const string SubAgentQueuedKey = "subagent.queued";
    public const string SubAgentRunningKey = "subagent.running";
    public const string SubAgentCompletedKey = "subagent.completed";
    public const string SubAgentStoppedKey = "subagent.stopped";
    public const string SubAgentFailedKey = "subagent.failed";
    public const string SubAgentEndedKey = "subagent.ended";
    public const string SubAgentWaitingSlotKey = "subagent.waitingSlot";
    public const string SubAgentSteeredKey = "subagent.steered";
    public const string SubAgentStopKey = "subagent.stop";
    public const string SubAgentStopNameKey = "subagent.stopName";
    public const string SubAgentStopTooltipKey = "subagent.stopTooltip";
    public const string SubAgentStopRequestedKey = "subagent.stopRequested";
    public const string SubAgentStoppedByUserKey = "subagent.stoppedByUser";
    public const string SubAgentTitleKey = "subagent.title";
    public const string SubAgentMainAgentKey = "subagent.mainAgent";
    public const string SubAgentFinishedKey = "subagent.finished";
    public const string SubAgentFinishedHintKey = "subagent.finishedHint";
    public const string SubAgentFinishedMenuKey = "subagent.finishedMenu";
    public const string SubAgentNoTranscriptKey = "subagent.noTranscript";
    public const string SubAgentReadOnlyBadgeKey = "subagent.readOnlyBadge";
    public const string SubAgentNoModelKey = "subagent.noModel";
    public const string SubAgentCollapseKey = "subagent.collapse";
    public const string SubAgentExpandKey = "subagent.expand";
    public const string SubAgentToggleKey = "subagent.toggle";
    public const string SubAgentRunningNameBase = "subagent.runningName";
    public const string SubAgentToggleNameKey = "subagent.toggleName";
    public const string SubAgentToggleTooltipKey = "subagent.toggleTooltip";
    public const string SubAgentReplayFailedKey = "subagent.replayFailed";
    public const string SubAgentFollowUpNoteKey = "subagent.followUpNote";

    /// <summary>Key base of the steered note, which inflects on the dropped-call count: see <see cref="LocText.Plural"/>.</summary>
    public const string SubAgentSteeredNoteBase = "subagent.steeredNote";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    // ---------------------------------------------------------------- session view
    public static string StartConversation => Loc.Instance[StartConversationKey];

    public static string ChatHint => Loc.Instance[ChatHintKey];

    public static string MessagePlaceholder => Loc.Instance[MessagePlaceholderKey];

    public static string MessageName => Loc.Instance[MessageNameKey];

    public static string Send => Loc.Instance[SendKey];

    public static string SendTooltip => Loc.Instance[SendTooltipKey];

    public static string StopTurn => Loc.Instance[StopTurnKey];

    public static string StopName => Loc.Instance[StopNameKey];

    /// <summary>A turn the user cancelled; stored as the card's footnote.</summary>
    public static LocText Stopped => new(StoppedKey);

    /// <summary>The notice shown when the stored history cannot be read.</summary>
    public static LocText ReadFailed(string message) => new(ReadFailedKey, message);

    /// <summary>The notice shown when the session started without a model provider.</summary>
    public static LocText ProviderMissing(string status) => new(ProviderMissingKey, status);

    // ---------------------------------------------------------------- composer
    public static string ComposerAgentDefault => Loc.Instance[ComposerAgentDefaultKey];

    public static string ComposerAgentDefaultDescription => Loc.Instance[ComposerAgentDefaultDescriptionKey];

    public static string ComposerSkill => Loc.Instance[ComposerSkillKey];

    public static string ComposerCommand => Loc.Instance[ComposerCommandKey];

    public static string ComposerQueued => Loc.Instance[ComposerQueuedKey];

    public static string ComposerInterjecting => Loc.Instance[ComposerInterjectingKey];

    public static string ComposerDefaultsSaved => Loc.Instance[ComposerDefaultsSavedKey];

    public static string ComposerModelsFetched(int count) =>
        Loc.Instance.Format(ComposerModelsFetchedKey, TextFormat.Number(count));

    public static string ComposerModelFetchFailed(string message) =>
        Loc.Instance.Format(ComposerModelFetchFailedKey, message);

    public static string ComposerEffortName(ThinkingEffort effort) => Loc.Instance[effort switch
    {
        ThinkingEffort.Low => ComposerEffortLowKey,
        ThinkingEffort.Medium => ComposerEffortMediumKey,
        ThinkingEffort.High => ComposerEffortHighKey,
        _ => ComposerEffortOffKey,
    }];

    public static LocText ComposerInterjectedNotice(string text) => new(ComposerInterjectedNoticeKey, text);
    // ---------------------------------------------------------------- turn card
    /// <summary>
    /// <c>第 3 轮 · 12:30:45 · 4.2 秒 · 输入 900 · 输出 80</c> / <c>Turn 3 · 12:30:45 · 4.2s · in 900 · out 80</c>; a side
    /// the provider did not report becomes <see cref="TextFormat.None"/>.
    /// </summary>
    public static string TurnHeader(
        int round,
        DateTimeOffset startedAt,
        TimeSpan? elapsed,
        bool running,
        int? inputTokens,
        int? outputTokens) =>
        Loc.Instance.Format(
            TurnHeaderKey,
            TextFormat.Number(round),
            TextFormat.Clock(startedAt),
            running ? TurnRunning : elapsed is { } value ? TextFormat.Elapsed(value) : TextFormat.None,
            TextFormat.Number(inputTokens),
            TextFormat.Number(outputTokens));

    public static string TurnRunning => Loc.Instance[TurnRunningKey];

    /// <summary><c>输入 900 · 输出 80</c>; a side the provider did not report becomes <see cref="TextFormat.None"/>.</summary>
    public static string TokenSummary(int? inputTokens, int? outputTokens) =>
        Loc.Instance.Format(TurnTokenSummaryKey, TokenCount(inputTokens), TokenCount(outputTokens));

    /// <summary>One token count, grouped; <see cref="TextFormat.None"/> when the provider did not report it.</summary>
    public static string TokenCount(int? tokens) => TextFormat.Number((long?)tokens);

    public static string CollapseTurn => Loc.Instance[TurnCollapseKey];

    public static string ToolArguments => Loc.Instance[ToolArgumentsKey];

    public static string ToolResult => Loc.Instance[ToolResultKey];

    /// <summary>The placeholder of a reply that never arrived; stored in the closed text block.</summary>
    public static LocText NoText => new(NoTextKey);

    /// <summary>What a tool row shows when its call never returned a result; stored as the row's result.</summary>
    public static LocText NoToolResult => new(NoToolResultKey);

    /// <summary>The notice shown when the turn itself could not run.</summary>
    public static LocText TurnFailed(string message) => new(TurnFailedKey, message);

    /// <summary>The notice shown in place of an approved-but-unavailable tool call.</summary>
    public static LocText Denied(string tool) => new(DeniedKey, tool);

    /// <summary>The notice shown after the context was compacted.</summary>
    public static LocText Compacted(int before, int after) => new(CompactedKey, TextFormat.Number(before), TextFormat.Number(after));

    /// <summary>The notice shown when the turn checkpoint could not be written.</summary>
    public static LocText CheckpointFailed(string message) => new(CheckpointFailedKey, message);

    /// <summary>The notice shown when the user steered the running turn.</summary>
    public static LocText SteeredNotice(string instruction) => new(SteeredNoticeKey, instruction);

    /// <summary>The notice shown when a follow-up instruction was queued behind the running turn.</summary>
    public static LocText FollowUpNotice(string instruction) => new(FollowUpNoticeKey, instruction);

    public static string StopReasonText(StopReason reason) =>
        Loc.Instance[reason switch
        {
            StopReason.EndTurn => StopReasonEndTurnKey,
            StopReason.ToolUse => StopReasonToolUseKey,
            StopReason.MaxTokens => StopReasonMaxTokensKey,
            _ => StopReasonUnknownKey,
        }];

    // ---------------------------------------------------------------- sub-agents
    public static string SubAgentQueued => Loc.Instance[SubAgentQueuedKey];

    public static string SubAgentRunning => Loc.Instance[SubAgentRunningKey];

    public static string SubAgentCompleted => Loc.Instance[SubAgentCompletedKey];

    public static string SubAgentStopped => Loc.Instance[SubAgentStoppedKey];

    public static string SubAgentFailed => Loc.Instance[SubAgentFailedKey];

    /// <summary>Shown for a stored sub-agent whose run outcome the store cannot prove.</summary>
    public static string SubAgentEnded => Loc.Instance[SubAgentEndedKey];

    /// <summary>The note of a queued sub-agent that has no run slot yet.</summary>
    public static LocText SubAgentWaitingSlot => new(SubAgentWaitingSlotKey);

    public static string SubAgentSteered => Loc.Instance[SubAgentSteeredKey];

    public static string SubAgentStop => Loc.Instance[SubAgentStopKey];

    public static string SubAgentStopName => Loc.Instance[SubAgentStopNameKey];

    public static string SubAgentStopTooltip => Loc.Instance[SubAgentStopTooltipKey];

    /// <summary>The note of a sub-agent the user asked to stop.</summary>
    public static LocText SubAgentStopRequested => new(SubAgentStopRequestedKey);

    /// <summary>
    /// The reason handed to the host when the user stops a sub-agent; it travels into the child's stored tool result, so it
    /// is resolved when the stop is requested rather than kept as a <see cref="LocText"/>.
    /// </summary>
    public static string SubAgentStoppedByUser => Loc.Instance[SubAgentStoppedByUserKey];

    public static string SubAgentsTitle => Loc.Instance[SubAgentTitleKey];

    public static string MainAgentTitle => Loc.Instance[SubAgentMainAgentKey];

    public static string FinishedAgentsTitle => Loc.Instance[SubAgentFinishedKey];

    public static string FinishedAgentsHint => Loc.Instance[SubAgentFinishedHintKey];

    /// <summary><c>已结束 (3)</c> / <c>Finished (3)</c>: the dropdown button of the finished sub-agents.</summary>
    public static string FinishedAgentsMenu(int count) =>
        Loc.Instance.Format(SubAgentFinishedMenuKey, TextFormat.Number(count));

    /// <summary>The note of a finished sub-agent whose stored conversation is empty; stored in its transcript.</summary>
    public static LocText FinishedAgentsNoTranscript => new(SubAgentNoTranscriptKey);

    public static string ReadOnlyBadge => Loc.Instance[SubAgentReadOnlyBadgeKey];

    public static string NoModel => Loc.Instance[SubAgentNoModelKey];

    public static string SubAgentsCollapse => Loc.Instance[SubAgentCollapseKey];

    public static string SubAgentsExpand => Loc.Instance[SubAgentExpandKey];

    /// <summary>
    /// The strip's sub-agent chip label. It is only the noun: the number of running sub-agents is the round badge beside
    /// it, and whether the area is collapsed is the button's active appearance instead of a word.
    /// </summary>
    public static string SubAgentsLabel => Loc.Instance[SubAgentToggleKey];

    /// <summary>The chip's name while sub-agents run: assistive technology replaces the button's content with it.</summary>
    public static string SubAgentsRunningName(int running) =>
        Loc.Instance.Format(LocText.PluralKey(SubAgentRunningNameBase, running), TextFormat.Number(running));

    public static string SubAgentsToggleName => Loc.Instance[SubAgentToggleNameKey];

    public static string SubAgentsToggleTooltip => Loc.Instance[SubAgentToggleTooltipKey];

    /// <summary>The notice shown when a finished sub-agent's stored conversation cannot be read.</summary>
    public static LocText SubAgentReplayFailed(string message) => new(SubAgentReplayFailedKey, message);

    /// <summary>The steered note of a sub-agent; inflects on the number of dropped calls.</summary>
    public static LocText SubAgentSteeredNote(int droppedToolCalls) =>
        LocText.Plural(SubAgentSteeredNoteBase, droppedToolCalls, TextFormat.Number(droppedToolCalls));

    /// <summary>The status note of a sub-agent with a follow-up instruction waiting.</summary>
    public static LocText SubAgentFollowUpNote(int pending) =>
        new(SubAgentFollowUpNoteKey, TextFormat.Number(pending));

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        // session view
        Loc.Register(StartConversationKey, "开始对话", "Start a conversation");
        Loc.Register(
            ChatHintKey,
            "输入消息后按 Ctrl+Enter 发送。工具调用与每一轮都会显示为卡片。",
            "Type a message and press Ctrl+Enter to send. Tool calls and every turn appear as cards.");
        Loc.Register(MessagePlaceholderKey, "描述要完成的任务…", "Describe the task…");
        Loc.Register(MessageNameKey, "消息", "Message");
        Loc.Register(SendKey, "发送", "Send");
        Loc.Register(SendTooltipKey, "发送 (Ctrl+Enter)", "Send (Ctrl+Enter)");
        Loc.Register(StopTurnKey, "停止本轮", "Stop this turn");
        Loc.Register(StopNameKey, "停止", "Stop");
        Loc.Register(ComposerAgentKey, "代理", "Agent");
        Loc.Register(ComposerAgentDefaultKey, "Agent", "Agent");
        Loc.Register(ComposerAgentDefaultDescriptionKey, "主编码代理（默认）", "Main coding agent (default)");
        Loc.Register(ComposerAgentTooltipKey, "选择本会话后续轮次使用的代理", "Choose the agent for the next turns of this session");
        Loc.Register(ComposerModelTooltipKey, "选择模型与思考力度", "Choose the model and thinking effort");
        Loc.Register(ComposerSearchModelsKey, "搜索模型", "Search models");
        Loc.Register(ComposerNoModelsKey, "没有匹配的模型", "No matching models");
        Loc.Register(ComposerPinKey, "固定到顶部", "Pin to top");
        Loc.Register(ComposerEffortKey, "力度", "Effort");
        Loc.Register(ComposerEffortOffKey, "关闭", "Off");
        Loc.Register(ComposerEffortLowKey, "低", "Low");
        Loc.Register(ComposerEffortMediumKey, "中", "Medium");
        Loc.Register(ComposerEffortHighKey, "高", "High");
        Loc.Register(ComposerSaveDefaultsKey, "新会话的默认值", "Defaults for new sessions");
        Loc.Register(ComposerDefaultsSavedKey, "已保存为新会话的默认值", "Saved as defaults for new sessions");
        Loc.Register(ComposerRefreshKey, "刷新", "Refresh");
        Loc.Register(ComposerModelsFetchedKey, "已加载 {0} 个模型", "Loaded {0} models");
        Loc.Register(ComposerModelFetchFailedKey, "拉取模型失败：{0}", "Could not fetch models: {0}");
        Loc.Register(ComposerRunningMenuKey, "本轮运行中：选择如何发送", "A turn is running: choose how to send");
        Loc.Register(ComposerInterjectKey, "插话", "Interject");
        Loc.Register(ComposerInterjectHintKey, "在下一批工具调用后交给正在运行的代理", "Hand to the running agent after its next tool batch");
        Loc.Register(ComposerQueueKey, "排队", "Queue");
        Loc.Register(ComposerQueueHintKey, "本轮结束后自动发送 (Ctrl+Enter)", "Send automatically when this turn ends (Ctrl+Enter)");
        Loc.Register(ComposerRedirectKey, "转弯", "Redirect");
        Loc.Register(ComposerRedirectHintKey, "停止本轮并立即发送", "Stop this turn and send right away");
        Loc.Register(ComposerStopHintKey, "停止本轮，暂停队列", "Stop this turn and pause the queue");
        Loc.Register(ComposerQueuedKey, "排队", "Queued");
        Loc.Register(ComposerInterjectingKey, "插话", "Interjecting");
        Loc.Register(ComposerRemoveQueuedKey, "移除", "Remove");
        Loc.Register(ComposerQueuePausedKey, "队列已暂停：发送或按 Ctrl+Enter 继续", "Queue paused: send or press Ctrl+Enter to resume");
        Loc.Register(ComposerInterjectedNoticeKey, "插话：{0}", "Interjected: {0}");
        Loc.Register(ComposerSkillKey, "技能", "Skill");
        Loc.Register(ComposerCommandKey, "命令", "Command");
        Loc.Register(ComposerNoMatchesKey, "没有匹配项", "No matches");        Loc.Register(StoppedKey, "已停止", "Stopped");
        Loc.Register(ReadFailedKey, "读取会话失败：{0}", "Could not read the session: {0}");
        Loc.Register(ProviderMissingKey, "尚未配置模型提供商：{0}", "No model provider is configured: {0}");

        // turn card and its notices
        Loc.Register(TurnHeaderKey, "第 {0} 轮 · {1} · {2} · 输入 {3} · 输出 {4}", "Turn {0} · {1} · {2} · in {3} · out {4}");
        Loc.Register(TurnRunningKey, "运行中…", "Running…");
        Loc.Register(TurnTokenSummaryKey, "输入 {0} · 输出 {1}", "in {0} · out {1}");
        Loc.Register(TurnCollapseKey, "折叠本轮", "Collapse this turn");
        Loc.Register(ToolArgumentsKey, "参数", "Arguments");
        Loc.Register(ToolResultKey, "结果", "Result");
        Loc.Register(NoTextKey, "（模型没有返回文本）", "(the model returned no text)");
        Loc.Register(NoToolResultKey, "没有结果：本轮已结束", "No result: the turn ended");
        Loc.Register(TurnFailedKey, "本轮无法运行：{0}", "This turn could not run: {0}");
        Loc.Register(DeniedKey, "已拒绝：{0}（尚未配置审批对话框）", "Denied: {0} (no approval dialog yet)");
        Loc.Register(CompactedKey, "已压缩上下文：{0} → {1} tokens", "Context compacted: {0} → {1} tokens");
        Loc.Register(CheckpointFailedKey, "轮次检查点失败：{0}", "Turn checkpoint failed: {0}");
        Loc.Register(SteeredNoticeKey, "已转向：{0}", "Steered: {0}");
        Loc.Register(FollowUpNoticeKey, "后续指令：{0}", "Follow-up: {0}");
        Loc.Register(StopReasonEndTurnKey, "正常结束", "Finished normally");
        Loc.Register(StopReasonToolUseKey, "请求工具", "Requested tools");
        Loc.Register(StopReasonMaxTokensKey, "达到长度上限", "Hit the length limit");
        Loc.Register(StopReasonUnknownKey, "其他原因", "Other reason");

        // sub-agents
        Loc.Register(SubAgentQueuedKey, "排队中", "Queued");
        Loc.Register(SubAgentRunningKey, "运行中", "Running");
        Loc.Register(SubAgentCompletedKey, "完成", "Done");
        Loc.Register(SubAgentStoppedKey, "已停止", "Stopped");
        Loc.Register(SubAgentFailedKey, "失败", "Failed");
        Loc.Register(SubAgentEndedKey, "已结束", "Finished");
        Loc.Register(SubAgentWaitingSlotKey, "等待运行名额", "Waiting for a run slot");
        Loc.Register(SubAgentSteeredKey, "已转向", "Steered");
        Loc.Register(SubAgentStopKey, "停止", "Stop");
        Loc.Register(SubAgentStopNameKey, "停止子 Agent", "Stop sub-agent");
        Loc.Register(
            SubAgentStopTooltipKey,
            "停止这个子 Agent，结果回报给主 Agent",
            "Stop this sub-agent; its result is reported to the main agent");
        Loc.Register(SubAgentStopRequestedKey, "已请求停止", "Stop requested");
        Loc.Register(SubAgentStoppedByUserKey, "已停止", "Stopped");
        Loc.Register(SubAgentTitleKey, "子 Agent", "Sub-agents");
        Loc.Register(SubAgentMainAgentKey, "主 Agent", "Main agent");
        Loc.Register(SubAgentFinishedKey, "已结束", "Finished");
        Loc.Register(
            SubAgentFinishedHintKey,
            "按结束顺序排列，选择一个查看只读记录。",
            "In finish order; pick one to see its read-only record.");
        Loc.Register(SubAgentFinishedMenuKey, "已结束 ({0})", "Finished ({0})");
        Loc.Register(SubAgentNoTranscriptKey, "这次子 Agent 没有存下可重放的消息。", "This sub-agent stored no messages to replay.");
        Loc.Register(SubAgentReadOnlyBadgeKey, "已结束", "Finished");
        Loc.Register(SubAgentNoModelKey, "未配置模型", "No model");
        Loc.Register(SubAgentCollapseKey, "收起", "Collapse");
        Loc.Register(SubAgentExpandKey, "展开", "Expand");
        Loc.Register(SubAgentToggleKey, "子 Agent", "Sub-agents");
        Loc.Register(
            LocText.PluralKey(SubAgentRunningNameBase, 1),
            "{0} 个子 Agent 正在运行",
            "{0} sub-agent running");
        Loc.Register(
            LocText.PluralKey(SubAgentRunningNameBase, 2),
            "{0} 个子 Agent 正在运行",
            "{0} sub-agents running");
        Loc.Register(SubAgentToggleNameKey, "子 Agent 区域", "Sub-agent area");
        Loc.Register(
            SubAgentToggleTooltipKey,
            "收起或展开子 Agent 区域；手动收起后本会话不再自动弹出",
            "Collapse or expand the sub-agent area; once collapsed by hand this session stops popping it up");
        Loc.Register(SubAgentReplayFailedKey, "重放子 Agent 会话失败：{0}", "Could not replay the sub-agent session: {0}");
        Loc.Register(
            LocText.PluralKey(SubAgentSteeredNoteBase, 1),
            "已转向 · 丢弃 {0} 个调用",
            "{0} call dropped");
        Loc.Register(
            LocText.PluralKey(SubAgentSteeredNoteBase, 2),
            "已转向 · 丢弃 {0} 个调用",
            "{0} calls dropped");
        Loc.Register(SubAgentFollowUpNoteKey, "后续指令排队 ({0})", "Follow-up queued ({0})");
    }
}
