using System.Globalization;
using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.App.ViewModels;

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

    // The prompt reload button (title bar) and the toast that points at it.
    public const string PromptsChangedTitleKey = "session.prompts.changedTitle";
    public const string PromptsChangedBodyKey = "session.prompts.changedBody";
    public const string PromptsReloadTipKey = "session.prompts.reloadTip";
    public const string PromptsReloadedKey = "session.prompts.reloaded";
    public const string PromptsReloadedTitleKey = "session.prompts.reloadedTitle";
    public const string PromptsReloadSkippedKey = "session.prompts.reloadSkipped";
    public const string PromptsReloadSkippedTitleKey = "session.prompts.reloadSkippedTitle";
    public const string ReadFailedKey = "session.readFailed";
    public const string ProviderMissingKey = "session.providerMissing";

    // ---------------------------------------------------------------- breadcrumb
    public const string BreadcrumbBackToParentKey = "session.breadcrumb.backToParent";

    // ---------------------------------------------------------------- composer
    public const string ComposerAgentKey = "session.composer.agent";
    public const string ComposerAgentDefaultKey = "session.composer.agentDefault";
    public const string ComposerAgentDefaultDescriptionKey = "session.composer.agentDefaultDescription";
    public const string ComposerAgentTooltipKey = "session.composer.agentTooltip";
    public const string ComposerOrchestrationKey = "session.composer.orchestration";
    public const string ComposerOrchestrationHintKey = "session.composer.orchestrationHint";
    public const string ComposerOrchestrationAutoKey = "session.composer.orchestrationAuto";
    public const string ComposerOrchestrationAutoDescriptionKey = "session.composer.orchestrationAutoDescription";
    public const string ComposerOrchestrationDirectKey = "session.composer.orchestrationDirect";
    public const string ComposerOrchestrationDirectDescriptionKey = "session.composer.orchestrationDirectDescription";
    public const string ComposerModelTooltipKey = "session.composer.modelTooltip";
    public const string ComposerSearchModelsKey = "session.composer.searchModels";
    public const string ComposerNoModelsKey = "session.composer.noModels";
    public const string ComposerPinKey = "session.composer.pin";
    public const string ComposerPinnedSectionKey = "session.composer.pinnedSection";
    public const string ComposerDefaultModelKey = "session.composer.defaultModel";
    public const string ComposerModelPriceKey = "session.composer.modelPrice";
    public const string ComposerEffortKey = "session.composer.effort";
    public const string ComposerEffortOffKey = "session.composer.effort.off";
    public const string ComposerEffortMinimalKey = "session.composer.effort.minimal";
    public const string ComposerEffortLowKey = "session.composer.effort.low";
    public const string ComposerEffortMediumKey = "session.composer.effort.medium";
    public const string ComposerEffortHighKey = "session.composer.effort.high";
    public const string ComposerEffortXHighKey = "session.composer.effort.xhigh";
    public const string ComposerEffortXHighCompactKey = "session.composer.effort.xhighCompact";
    public const string ComposerEffortMaxKey = "session.composer.effort.max";
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
    public const string ComposerWithdrawQueuedKey = "session.composer.withdrawQueued";
    public const string ComposerInterjectDeliveredKey = "session.composer.interjectDelivered";
    public const string ComposerWithdrawAttachmentsFullKey = "session.composer.withdrawAttachmentsFull";
    public const string ComposerQueuePausedKey = "session.composer.queuePaused";
    public const string ComposerInterjectedNoticeKey = "session.composer.interjectedNotice";
    public const string ComposerInterjectedAttachmentsBase = "session.composer.interjectedAttachments";
    public const string ComposerInterjectedTextAttachmentsBase = "session.composer.interjectedTextAttachments";
    public const string ComposerAttachmentImageKey = "session.composer.attachmentImage";
    public const string ComposerAttachmentSeparatorKey = "session.composer.attachmentSeparator";
    public const string ComposerSkillKey = "session.composer.skill";
    public const string ComposerCommandKey = "session.composer.command";
    public const string ComposerNoMatchesKey = "session.composer.noMatches";
    // ---------------------------------------------------------------- turn card and the notices around it
    public const string TurnWorkingKey = "turn.working";
    public const string TurnCollapseKey = "turn.collapse";
    public const string TurnExpandKey = "turn.expand";
    public const string ToolArgumentsKey = "turn.toolArguments";
    public const string ToolResultKey = "turn.toolResult";
    public const string ToolOpenFileKey = "turn.toolOpenFile";
    public const string ToolCopyResultKey = "turn.toolCopyResult";
    public const string ToolShowMoreKey = "turn.toolShowMore";
    public const string ToolShowLessKey = "turn.toolShowLess";
    public const string ToolResultCutKey = "turn.toolResultCut";
    public const string NoTextKey = "turn.noText";
    public const string CopyMessageKey = "turn.copyMessage";
    public const string NoToolResultKey = "turn.noToolResult";
    public const string TurnFailedKey = "turn.failed";
    public const string TurnRetryKey = "turn.retry";
    public const string TurnRetryNameKey = "turn.retryName";
    public const string DeniedKey = "turn.denied";
    public const string CompactedKey = "turn.compacted";
    public const string ProviderNoticeTitleKey = "turn.providerNotice.title";
    public const string CacheRewriteTitleKey = "turn.cacheRewrite.title";
    public const string CacheRewriteBodyKey = "turn.cacheRewrite.body";
    public const string CheckpointFailedKey = "turn.checkpointFailed";
    public const string SteeredNoticeKey = "turn.steeredNotice";
    public const string FollowUpNoticeKey = "turn.followUpNotice";
    public const string StopReasonEndTurnKey = "turn.stopReason.endTurn";
    public const string StopReasonToolUseKey = "turn.stopReason.toolUse";
    public const string StopReasonMaxTokensKey = "turn.stopReason.maxTokens";
    public const string StopReasonUnknownKey = "turn.stopReason.unknown";
    public const string UserTurnRoundKey = "turn.user.round";
    public const string UserTurnGapKey = "turn.user.gap";
    public const string TurnDividerKey = "turn.divider";
    public const string TurnDividerFailedKey = "turn.divider.failed";
    public const string TurnStatusCompletedKey = "turn.status.completed";
    public const string TurnStatusStoppedKey = "turn.status.stopped";
    public const string TurnStatusFailedKey = "turn.status.failed";
    public const string TurnStatusLimitKey = "turn.status.limit";
    public const string TurnDividerTipStateKey = "turn.dividerTip.state";
    public const string TurnDividerTipUsageKey = "turn.dividerTip.usage";
    public const string TurnDividerTipCacheReadKey = "turn.dividerTip.cacheRead";
    public const string TurnDividerTipCacheWriteKey = "turn.dividerTip.cacheWrite";
    public const string TurnDividerTipModelKey = "turn.dividerTip.model";

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

    /// <summary>The sub-agent breadcrumb's parent segment: its tooltip and its accessible name.</summary>
    public static string BreadcrumbBackToParent(string parent) =>
        Loc.Instance.Format(BreadcrumbBackToParentKey, parent);

    public static string MessagePlaceholder => Loc.Instance[MessagePlaceholderKey];

    public static string MessageName => Loc.Instance[MessageNameKey];

    public static string Send => Loc.Instance[SendKey];

    public static string SendTooltip => Loc.Instance[SendTooltipKey];

    public static string StopTurn => Loc.Instance[StopTurnKey];

    public static string StopName => Loc.Instance[StopNameKey];

    /// <summary>A turn the user cancelled; stored as the card's footnote.</summary>
    public static LocText Stopped => new(StoppedKey);

    public static LocText PromptsChangedTitle => new(PromptsChangedTitleKey);

    public static LocText PromptsChangedBody(string title) => new(PromptsChangedBodyKey, title);

    public static LocText PromptsReloaded => new(PromptsReloadedKey);

    public static LocText PromptsReloadedTitle => new(PromptsReloadedTitleKey);

    public static LocText PromptsReloadSkipped => new(PromptsReloadSkippedKey);

    public static LocText PromptsReloadSkippedTitle => new(PromptsReloadSkippedTitleKey);

    /// <summary>The notice shown when the stored history cannot be read.</summary>
    public static LocText ReadFailed(string message) => new(ReadFailedKey, message);

    /// <summary>The notice shown when the session started without a model provider.</summary>
    public static LocText ProviderMissing(string status) => new(ProviderMissingKey, status);

    // ---------------------------------------------------------------- composer
    public static string ComposerAgentDefault => Loc.Instance[ComposerAgentDefaultKey];

    public static string ComposerAgentDefaultDescription => Loc.Instance[ComposerAgentDefaultDescriptionKey];

    public static string ComposerOrchestrationAuto => Loc.Instance[ComposerOrchestrationAutoKey];

    public static string ComposerOrchestrationAutoDescription => Loc.Instance[ComposerOrchestrationAutoDescriptionKey];

    public static string ComposerOrchestrationDirect => Loc.Instance[ComposerOrchestrationDirectKey];

    public static string ComposerOrchestrationDirectDescription => Loc.Instance[ComposerOrchestrationDirectDescriptionKey];

    public static string ComposerSkill => Loc.Instance[ComposerSkillKey];

    public static string ComposerCommand => Loc.Instance[ComposerCommandKey];

    public static string ComposerQueued => Loc.Instance[ComposerQueuedKey];

    public static string ComposerInterjecting => Loc.Instance[ComposerInterjectingKey];

    /// <summary>Why a pending interjection could not be withdrawn any more.</summary>
    public static LocText ComposerInterjectDelivered => new(ComposerInterjectDeliveredKey);

    /// <summary>Why a waiting message with attachments stays in the queue: the composer is full.</summary>
    public static LocText ComposerWithdrawAttachmentsFull => new(ComposerWithdrawAttachmentsFullKey);

    public static string ComposerDefaultsSaved => Loc.Instance[ComposerDefaultsSavedKey];

    public static string ComposerModelsFetched(int count) =>
        Loc.Instance.Format(ComposerModelsFetchedKey, TextFormat.Number(count));

    public static string ComposerModelFetchFailed(string message) =>
        Loc.Instance.Format(ComposerModelFetchFailedKey, message);

    public static string ComposerEffortName(ThinkingEffort effort) => Loc.Instance[effort switch
    {
        ThinkingEffort.Minimal => ComposerEffortMinimalKey,
        ThinkingEffort.Low => ComposerEffortLowKey,
        ThinkingEffort.Medium => ComposerEffortMediumKey,
        ThinkingEffort.High => ComposerEffortHighKey,
        ThinkingEffort.XHigh => ComposerEffortXHighKey,
        ThinkingEffort.Max => ComposerEffortMaxKey,
        _ => ComposerEffortOffKey,
    }];

    /// <summary>
    /// The step's name for a narrow label — the model picker's tick row, where a seventh of the track is all a label gets.
    /// Only XHigh differs ("Extra high" does not fit); every roomier surface keeps <see cref="ComposerEffortName"/>.
    /// </summary>
    public static string ComposerEffortTickName(ThinkingEffort effort) => effort == ThinkingEffort.XHigh
        ? Loc.Instance[ComposerEffortXHighCompactKey]
        : ComposerEffortName(effort);

    /// <summary>The pinned section's caption, above the models the user pinned.</summary>
    public static string ComposerPinnedSection => Loc.Instance[ComposerPinnedSectionKey];

    /// <summary>The hint behind the icon on the row a new session starts with.</summary>
    public static string ComposerDefaultModelHint => Loc.Instance[ComposerDefaultModelKey];

    /// <summary>
    /// The price line on a model picker row: input and output price per million tokens, then the context window, e.g.
    /// "每百万 $0.15 / $0.6 · 1M". Whatever is unknown is left out rather than shown as zero; nothing known gives an empty
    /// string, and the row then shows no line at all.
    /// </summary>
    public static string ComposerModelMeta(ModelMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var price = metadata.Pricing is { } pricing && (pricing.Input > 0 || pricing.Output > 0)
            ? Loc.Instance.Format(ComposerModelPriceKey, Amount(pricing.Input), Amount(pricing.Output))
            : string.Empty;
        var window = metadata.ContextWindow is { } tokens && tokens > 0 ? Window(tokens) : string.Empty;
        return price.Length == 0 ? window
            : window.Length == 0 ? price
            : price + " · " + window;
    }

    /// <summary>A price with no trailing zeros: 0.15, 0.6, 15.</summary>
    private static string Amount(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A context window in the compact form the instruments use: 200k, 1M, 1.5M.</summary>
    private static string Window(int tokens) => tokens switch
    {
        >= 1_000_000 => (tokens / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (tokens / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };

    public static LocText ComposerInterjectedNotice(string text) => new(ComposerInterjectedNoticeKey, text);

    /// <summary>
    /// The notice of a delivered interjection. Text only: "插话：<text>". Attachments only: "插话：2 个附件（a.png、report.pdf）",
    /// an image without a name reading as "图片". Text and attachments: the text plus the attachment count. The names are
    /// resolved when the notice renders, so the placeholder follows a language switch.
    /// </summary>
    public static LocText ComposerInterjectedNotice(string text, IReadOnlyList<ContentBlock> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0)
        {
            return ComposerInterjectedNotice(text);
        }

        var count = TextFormat.Number(attachments.Count);
        return string.IsNullOrWhiteSpace(text)
            ? LocText.Plural(ComposerInterjectedAttachmentsBase, attachments.Count, count, new AttachmentNames(attachments))
            : LocText.Plural(ComposerInterjectedTextAttachmentsBase, attachments.Count, text, count);
    }

    /// <summary>
    /// The display names of an interjection's attachments, joined in the language of the moment: a file's own name (its
    /// media type when it has none), an inlined text file's name, and the image placeholder for an image.
    /// </summary>
    private sealed class AttachmentNames(IReadOnlyList<ContentBlock> attachments)
    {
        public override string ToString()
        {
            var names = new string[attachments.Count];
            for (var index = 0; index < names.Length; index++)
            {
                names[index] = attachments[index] switch
                {
                    FileBlock file => string.IsNullOrWhiteSpace(file.Name) ? file.MediaType : file.Name,
                    TextBlock block when Forge.Core.Storage.TextAttachment.TryParse(block.Text, out var name, out _) => name,
                    _ => Loc.Instance[ComposerAttachmentImageKey],
                };
            }

            return string.Join(Loc.Instance[ComposerAttachmentSeparatorKey], names);
        }
    }
    // ---------------------------------------------------------------- turn card
    /// <summary>The row a busy turn shows while nothing has arrived yet: what it is doing right now.</summary>
    public static string TurnWorking => Loc.Instance[TurnWorkingKey];

    public static string CollapseTurn => Loc.Instance[TurnCollapseKey];

    public static string ExpandTurn => Loc.Instance[TurnExpandKey];

    public static string ToolArguments => Loc.Instance[ToolArgumentsKey];

    public static string ToolResult => Loc.Instance[ToolResultKey];

    public static string ToolShowMore => Loc.Instance[ToolShowMoreKey];

    public static string ToolShowLess => Loc.Instance[ToolShowLessKey];

    /// <summary>The note under a result that was cut for display; copying still gives the whole output.</summary>
    public static string ToolResultCut(int shown, int total) => Loc.Instance.Format(ToolResultCutKey, shown, total);

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

    /// <summary>Toast title for a note the provider attached to an answer (a reduced request shape, for one).</summary>
    public static LocText ProviderNoticeTitle => new(ProviderNoticeTitleKey);

    /// <summary>Toast title for "the same prefix was written from scratch three times".</summary>
    public static LocText CacheRewriteTitle => new(CacheRewriteTitleKey);

    public static LocText CacheRewriteBody(long tokens) => new(CacheRewriteBodyKey, TextFormat.Number(tokens));

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

    /// <summary>The round marker of the meta line under a main-session user message: <c>第 2 轮</c> / <c>Turn 2</c>.</summary>
    public static string UserTurnRound(int round) =>
        Loc.Instance.Format(UserTurnRoundKey, TextFormat.Number(round));

    /// <summary>How long after the previous user message this round started: <c>12 分钟后</c> / <c>12m 00s later</c>.</summary>
    public static string TurnGap(string elapsed) => Loc.Instance.Format(UserTurnGapKey, elapsed);

    /// <summary>
    /// The round-end divider's line: <c>46.2 秒 · ↑37,000 ↓2,500 · 工具 6 · 54 tok/s</c>. Unknown sides read
    /// <see cref="TextFormat.None"/>; units and arrows are symbols, identical in both languages.
    /// </summary>
    public static string TurnDivider(string elapsed, string input, string output, int tools, string rate) =>
        Loc.Instance.Format(TurnDividerKey, elapsed, input, output, TextFormat.Number(tools), rate);

    /// <summary>
    /// The divider of a turn that did not complete: its status with the tokens as unknown, never zero.
    /// </summary>
    public static string TurnDividerFailed(string status, string elapsed, int tools) =>
        Loc.Instance.Format(TurnDividerFailedKey, status, elapsed, TextFormat.None, TextFormat.Number(tools));

    /// <summary>One word for how the turn ended, for the divider's line and tooltip.</summary>
    public static string TurnStatusText(TurnEndStatus status) =>
        Loc.Instance[status switch
        {
            TurnEndStatus.Stopped => TurnStatusStoppedKey,
            TurnEndStatus.Failed => TurnStatusFailedKey,
            TurnEndStatus.Limit => TurnStatusLimitKey,
            _ => TurnStatusCompletedKey,
        }];

    /// <summary>The divider tooltip's first line: how the turn ended, with the user-visible error when it left one, and the
    /// wall clock that times it.</summary>
    public static string DividerTipState(string status, string elapsed) =>
        Loc.Instance.Format(TurnDividerTipStateKey, status, elapsed);

    /// <summary>The tooltip's usage line: the two tokens with the cost that was priced for them.</summary>
    public static string DividerTipUsage(string input, string output, string cost) =>
        Loc.Instance.Format(TurnDividerTipUsageKey, input, output, cost);

    /// <summary>The tooltip's cache-read line: the read column and the hit rate computed from it.</summary>
    public static string DividerTipCacheRead(string cacheRead, string hitRate) =>
        Loc.Instance.Format(TurnDividerTipCacheReadKey, cacheRead, hitRate);

    /// <summary>The tooltip's cache-write line: the write column and its one-hour share.</summary>
    public static string DividerTipCacheWrite(string cacheWrite, string cacheWrite1h) =>
        Loc.Instance.Format(TurnDividerTipCacheWriteKey, cacheWrite, cacheWrite1h);

    /// <summary>The tooltip's model line: the provider and the model that ran the turn.</summary>
    public static string DividerTipModel(string model) =>
        Loc.Instance.Format(TurnDividerTipModelKey, model);

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
    public static string SubAgentsRunningName(int running) => Loc.Instance.FormatPlural(SubAgentRunningNameBase, running);

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
            "输入消息后按 Enter 发送，Shift+Enter 换行。工具调用与每一轮都会显示为卡片。",
            "Type a message and press Enter to send, Shift+Enter for a new line. Tool calls and every turn appear as cards.");
        Loc.Register(MessagePlaceholderKey, "描述要完成的任务…", "Describe the task…");
        Loc.Register(MessageNameKey, "消息", "Message");
        Loc.Register(SendKey, "发送", "Send");
        Loc.Register(SendTooltipKey, "发送 (Enter)", "Send (Enter)");
        Loc.Register(StopTurnKey, "停止本轮", "Stop this turn");
        Loc.Register(StopNameKey, "停止", "Stop");
        Loc.Register(ComposerAgentKey, "代理", "Agent");
        Loc.Register(ComposerAgentDefaultKey, "Agent", "Agent");
        Loc.Register(ComposerAgentDefaultDescriptionKey, "主编码代理（默认）", "Main coding agent (default)");
        Loc.Register(ComposerOrchestrationKey, "编排", "Orchestration");
        Loc.Register(ComposerOrchestrationHintKey, "仅对下一条消息生效，发送后恢复为自动", "Applies to the next message only, then returns to Auto");
        Loc.Register(ComposerOrchestrationAutoKey, "自动", "Auto");
        Loc.Register(ComposerOrchestrationAutoDescriptionKey, "由代理按默认路由决定是否走工作流", "The agent routes the work as usual");
        Loc.Register(ComposerOrchestrationDirectKey, "直接执行", "Direct");
        Loc.Register(ComposerOrchestrationDirectDescriptionKey, "跳过工作流，直接处理这条消息", "Skip workflows and handle this message directly");
        Loc.Register(ComposerAgentTooltipKey, "选择本会话后续轮次使用的代理", "Choose the agent for the next turns of this session");
        Loc.Register(ComposerModelTooltipKey, "选择模型与思考力度", "Choose the model and thinking effort");
        Loc.Register(ComposerSearchModelsKey, "搜索模型", "Search models");
        Loc.Register(ComposerNoModelsKey, "没有匹配的模型", "No matching models");
        Loc.Register(ComposerPinKey, "固定到顶部", "Pin to top");
        Loc.Register(ComposerPinnedSectionKey, "收藏", "Pinned");
        Loc.Register(ComposerDefaultModelKey, "新会话的默认模型", "The model new sessions start with");
        Loc.Register(ComposerModelPriceKey, "每百万 ${0} / ${1}", "per 1M ${0} / ${1}");
        Loc.Register(ComposerEffortKey, "推理", "Reasoning");
        Loc.Register(ComposerEffortOffKey, "关闭", "Off");
        Loc.Register(ComposerEffortMinimalKey, "极低", "Minimal");
        Loc.Register(ComposerEffortLowKey, "低", "Low");
        Loc.Register(ComposerEffortMediumKey, "中", "Medium");
        Loc.Register(ComposerEffortHighKey, "高", "High");
        Loc.Register(ComposerEffortXHighKey, "超高", "Extra high");
        Loc.Register(ComposerEffortXHighCompactKey, "超高", "XHigh");
        Loc.Register(ComposerEffortMaxKey, "最高", "Max");
        Loc.Register(ComposerSaveDefaultsKey, "设为默认", "Set default");
        Loc.Register(ComposerDefaultsSavedKey, "已保存为新会话的默认值", "Saved as defaults for new sessions");
        Loc.Register(ComposerRefreshKey, "刷新", "Refresh");
        Loc.Register(ComposerModelsFetchedKey, "已加载 {0} 个模型", "Loaded {0} models");
        Loc.Register(ComposerModelFetchFailedKey, "拉取模型失败：{0}", "Could not fetch models: {0}");
        Loc.Register(ComposerRunningMenuKey, "本轮运行中：选择如何发送", "A turn is running: choose how to send");
        Loc.Register(ComposerInterjectKey, "插话", "Interject");
        Loc.Register(ComposerInterjectHintKey, "在下一批工具调用后交给正在运行的代理 (Enter)", "Hand to the running agent after its next tool batch (Enter)");
        Loc.Register(ComposerQueueKey, "排队", "Queue");
        Loc.Register(ComposerQueueHintKey, "本轮结束后自动发送 (Ctrl+Enter)", "Send automatically when this turn ends (Ctrl+Enter)");
        Loc.Register(ComposerRedirectKey, "转弯", "Redirect");
        Loc.Register(ComposerRedirectHintKey, "停止本轮并立即发送 (Ctrl+Shift+Enter)", "Stop this turn and send right away (Ctrl+Shift+Enter)");
        Loc.Register(ComposerStopHintKey, "停止本轮，暂停队列", "Stop this turn and pause the queue");
        Loc.Register(ComposerQueuedKey, "排队", "Queued");
        Loc.Register(ComposerInterjectingKey, "插话", "Interjecting");
        Loc.Register(ComposerWithdrawQueuedKey, "撤回并编辑", "Withdraw and edit");
        Loc.Register(
            ComposerInterjectDeliveredKey,
            "这条插话已经送达模型，无法撤回；下一条在送达前可以撤回",
            "This interjection already reached the model and cannot be withdrawn; the next one can be withdrawn before delivery");
        Loc.Register(
            ComposerWithdrawAttachmentsFullKey,
            "输入框里的附件已满，先移除一些再撤回这条消息",
            "The composer already holds the maximum attachments; remove some before withdrawing this message");
        Loc.Register(ComposerQueuePausedKey, "队列已暂停：发送或按 Ctrl+Enter 继续", "Queue paused: send or press Ctrl+Enter to resume");
        Loc.Register(ComposerInterjectedNoticeKey, "插话：{0}", "Interjected: {0}");
        Loc.Register(ComposerInterjectedAttachmentsBase + ".one", "插话：{0} 个附件（{1}）", "Interjected: {0} attachment ({1})");
        Loc.Register(ComposerInterjectedAttachmentsBase + ".other", "插话：{0} 个附件（{1}）", "Interjected: {0} attachments ({1})");
        Loc.Register(ComposerInterjectedTextAttachmentsBase + ".one", "插话：{0}（另附 {1} 个附件）", "Interjected: {0} (+{1} attachment)");
        Loc.Register(ComposerInterjectedTextAttachmentsBase + ".other", "插话：{0}（另附 {1} 个附件）", "Interjected: {0} (+{1} attachments)");
        Loc.Register(ComposerAttachmentImageKey, "图片", "image");
        Loc.Register(ComposerAttachmentSeparatorKey, "、", ", ");
        Loc.Register(ComposerSkillKey, "技能", "Skill");
        Loc.Register(ComposerCommandKey, "命令", "Command");
        Loc.Register(ComposerNoMatchesKey, "没有匹配项", "No matches");
        Loc.Register(StoppedKey, "已停止", "Stopped");
        Loc.Register(PromptsChangedTitleKey, "提示词文件已更改", "Prompt files changed");
        Loc.Register(
            PromptsChangedBodyKey,
            "会话「{0}」使用的提示词有改动，但仍在用旧快照。可点标题栏的“重载提示词”按钮刷新，代价是该会话的提示缓存失效一次、下一轮按未缓存计费；也可以手动新开会话。",
            "Prompts used by \"{0}\" changed, but the session still runs on the old snapshot. The reload-prompts button in the title bar refreshes it, at the cost of losing this session's prompt cache once (the next turn is billed uncached); you can also open a new session by hand.");
        Loc.Register(PromptsReloadTipKey, "重载提示词（提示缓存会失效一次）", "Reload prompts (the prompt cache is lost once)");
        Loc.Register(PromptsReloadedTitleKey, "提示词已刷新", "Prompts reloaded");
        Loc.Register(PromptsReloadSkippedTitleKey, "暂未重载提示词", "Prompts not reloaded");
        Loc.Register(
            PromptsReloadSkippedKey,
            "回合进行中或会话尚未开始，没有可重载的快照：新会话的第一条消息会读取最新提示词，回合结束后可再点一次。",
            "A turn is running or the session has not started, so there is no snapshot to reload: the first message of a new session reads the latest prompts, and the button can be used again once the turn ends.");
        Loc.Register(
            PromptsReloadedKey,
            "下一条消息起使用新提示词，提示缓存已失效一次。为避免长会话反复浪费成本，建议手动新开会话。",
            "The next message uses the new prompts and the prompt cache was lost once. To avoid wasting cost on a long session, consider opening a new session by hand.");
        Loc.Register(ReadFailedKey, "读取会话失败：{0}", "Could not read the session: {0}");
        Loc.Register(ProviderMissingKey, "尚未配置模型提供商：{0}", "No model provider is configured: {0}");
        Loc.Register(BreadcrumbBackToParentKey, "返回父会话 {0}", "Back to {0}");

        // turn card and its notices
        Loc.Register(TurnCollapseKey, "折叠本轮", "Collapse this turn");
        Loc.Register(TurnExpandKey, "展开本轮", "Expand this turn");
        Loc.Register(TurnWorkingKey, "正在处理下一步…", "Working on the next step…");
        Loc.Register(TurnCollapseKey, "折叠本轮", "Collapse this turn");
        Loc.Register(ToolArgumentsKey, "参数", "Arguments");
        Loc.Register(ToolResultKey, "结果", "Result");
        Loc.Register(ToolOpenFileKey, "打开文件", "Open file");
        Loc.Register(ToolCopyResultKey, "复制结果", "Copy result");
        Loc.Register(ToolShowMoreKey, "展开全部", "Show more");
        Loc.Register(ToolShowLessKey, "收起", "Show less");
        Loc.Register(ToolResultCutKey, "仅显示前 {0} 个字符（共 {1} 个），复制可得完整输出", "Showing the first {0} of {1} characters; copy gives the whole output");
        Loc.Register(NoTextKey, "（模型没有返回文本）", "(the model returned no text)");
        Loc.Register(CopyMessageKey, "复制消息", "Copy message");
        Loc.Register(NoToolResultKey, "没有结果：本轮已结束", "No result: the turn ended");
        Loc.Register(TurnFailedKey, "本轮无法运行：{0}", "This turn could not run: {0}");
        Loc.Register(TurnRetryKey, "重试", "Retry");
        Loc.Register(TurnRetryNameKey, "重试这一轮（重新发送同一条消息）", "Retry this turn (sends the same message again)");
        Loc.Register(DeniedKey, "已拒绝：{0}（尚未配置审批对话框）", "Denied: {0} (no approval dialog yet)");
        Loc.Register(CompactedKey, "已压缩上下文：{0} → {1} tokens", "Context compacted: {0} → {1} tokens");
        Loc.Register(ProviderNoticeTitleKey, "本轮请求的形状被降级", "This turn ran with a reduced request");
        Loc.Register(CacheRewriteTitleKey, "提示词缓存被反复整段重写", "The prompt cache was rewritten from scratch repeatedly");
        Loc.Register(
            CacheRewriteBodyKey,
            "一小时内已有 3 次整段重写，共写入 {0} 个 token（按缓存写入价计费）。通常是工具集、模型或系统提示在这一小时内变过，而不是缓存寿命太短",
            "Three full rewrites in one hour, {0} tokens written in total at the cache-write price. Usually the tool set, the model or the system prompt changed within the hour rather than the cache lifetime being too short");
        Loc.Register(CheckpointFailedKey, "轮次检查点失败：{0}", "Turn checkpoint failed: {0}");
        Loc.Register(SteeredNoticeKey, "已转向：{0}", "Steered: {0}");
        Loc.Register(FollowUpNoticeKey, "后续指令：{0}", "Follow-up: {0}");
        Loc.Register(StopReasonEndTurnKey, "正常结束", "Finished normally");
        Loc.Register(StopReasonToolUseKey, "请求工具", "Requested tools");
        Loc.Register(StopReasonMaxTokensKey, "达到长度上限", "Hit the length limit");
        Loc.Register(StopReasonUnknownKey, "其他原因", "Other reason");
        Loc.Register(UserTurnRoundKey, "第 {0} 轮", "Turn {0}");
        Loc.Register(UserTurnGapKey, "{0} 后", "{0} later");
        Loc.Register(TurnDividerKey, "{0} · ↑{1} ↓{2} · 工具 {3} · {4}", "{0} · ↑{1} ↓{2} · {3} tools · {4}");
        Loc.Register(TurnDividerFailedKey, "{0} · {1} · ↑{2} ↓{2} · 工具 {3}", "{0} · {1} · ↑{2} ↓{2} · {3} tools");
        Loc.Register(TurnStatusCompletedKey, "完成", "Done");
        Loc.Register(TurnStatusStoppedKey, "已停止", "Stopped");
        Loc.Register(TurnStatusFailedKey, "失败", "Failed");
        Loc.Register(TurnStatusLimitKey, "超限", "Limit");
        Loc.Register(TurnDividerTipStateKey, "状态: {0} · 耗时: {1}", "Status: {0} · Elapsed: {1}");
        Loc.Register(TurnDividerTipUsageKey, "输入: {0} · 输出: {1} · 成本: {2}", "In: {0} · Out: {1} · Cost: {2}");
        Loc.Register(TurnDividerTipCacheReadKey, "缓存读: {0} · 缓存命中率: {1}", "Cache read: {0} · Cache hit rate: {1}");
        Loc.Register(TurnDividerTipCacheWriteKey, "缓存写: {0} · 一小时写: {1}", "Cache write: {0} · 1h write: {1}");
        Loc.Register(TurnDividerTipModelKey, "模型: {0}", "Model: {0}");

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
        Loc.Register(SubAgentFinishedKey, "SubAgent 历史会话", "Sub-agent history");
        Loc.Register(
            SubAgentFinishedHintKey,
            "本会话的子 Agent 历史会话，按最近一次运行排序；同一子 Agent 被再次领任务时会先回到活标签。选择一个查看只读记录。",
            "The session's sub-agent sessions, ordered by their latest run; one handed a new task returns to the live tabs first. Pick one to see its read-only record.");
        Loc.Register(SubAgentFinishedMenuKey, "SubAgent 历史会话 ({0})", "Sub-agent history ({0})");
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
