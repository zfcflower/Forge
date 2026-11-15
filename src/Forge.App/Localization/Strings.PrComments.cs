using System.Runtime.CompilerServices;
using Forge.App.ViewModels;

namespace Forge.App.Localization;

/// <summary>
/// The pull request review-thread panel's own string table (wave W16a, L2): the panel chrome, the three thread filters,
/// one thread's location and state, the reply box, resolve and the hand-off to the agent. Keys are namespaced
/// <c>prComments.*</c> and every text is registered from a module initializer, so no shared table is edited here.
/// <para>Texts with a value are format strings read through the typed accessors below; the comment bodies themselves are
/// user text and never translated. The agent-facing hand-off message is English and lives in
/// <c>ViewModels/PrCommentsHandOffMessage.cs</c>: it is model input, not UI.</para>
/// </summary>
internal static class PrCommentsStrings
{
    // ---------------------------------------------------------------- panel chrome

    public const string Title = "prComments.title";
    public const string Number = "prComments.number";
    public const string ButtonRefresh = "prComments.button.refresh";
    public const string ButtonClose = "prComments.button.close";

    /// <summary>Opens the pull request this review belongs to in the browser.</summary>
    public const string ButtonOpenPullRequest = "prComments.button.openPullRequest";

    /// <summary>A read is in flight: <c>prComments.read.reading</c>.</summary>
    public const string ReadReading = "prComments.read.reading";

    /// <summary>Why the threads could not be read: <c>{0}</c> is the platform's own message.</summary>
    public const string ReadFailed = "prComments.read.failed";

    /// <summary>When the list was read: <c>{0}</c> is a clock time.</summary>
    public const string ReadAt = "prComments.read.at";

    // ---------------------------------------------------------------- the filters

    /// <summary>The three filter chips; <c>{0}</c> is the count of threads behind each.</summary>
    public const string FilterOpen = "prComments.filter.open";
    public const string FilterResolved = "prComments.filter.resolved";
    public const string FilterOutdated = "prComments.filter.outdated";

    /// <summary>The empty state of the selected filter, one per filter.</summary>
    public const string EmptyOpen = "prComments.empty.open";
    public const string EmptyResolved = "prComments.empty.resolved";
    public const string EmptyOutdated = "prComments.empty.outdated";

    /// <summary>What the empty state says about getting a new reading.</summary>
    public const string EmptyBody = "prComments.empty.body";

    // ---------------------------------------------------------------- one thread

    /// <summary>Where the thread sits in the diff: <c>{0}</c> path, <c>{1}</c> line.</summary>
    public const string Location = "prComments.thread.location";

    /// <summary>The same for a thread the diff no longer has, where the line is the one it was first placed on.</summary>
    public const string LocationOriginal = "prComments.thread.location.original";

    /// <summary>A thread the platform reported without a path (a file-level or a deleted-file comment).</summary>
    public const string LocationUnknown = "prComments.thread.location.unknown";

    public const string Resolved = "prComments.thread.resolved";
    public const string Outdated = "prComments.thread.outdated";

    /// <summary>Who resolved the thread: <c>{0}</c> is the login.</summary>
    public const string ResolvedBy = "prComments.thread.resolvedBy";

    /// <summary>How many comments a thread holds: <c>{0}</c> is the count.</summary>
    public const string CommentCount = "prComments.thread.commentCount";

    /// <summary>The author the platform did not report.</summary>
    public const string Anonymous = "prComments.thread.anonymous";

    // ---------------------------------------------------------------- the actions

    public const string ButtonReply = "prComments.button.reply";
    public const string ButtonResolve = "prComments.button.resolve";
    public const string ButtonReopen = "prComments.button.reopen";

    /// <summary>Why 重开 is disabled: the platform interface this build uses only resolves threads.</summary>
    public const string ReopenUnavailable = "prComments.reopen.unavailable";

    /// <summary>Hands this thread to the open session's agent as one message.</summary>
    public const string ButtonHandOff = "prComments.button.handOff";

    public const string ReplyPlaceholder = "prComments.reply.placeholder";

    /// <summary>An empty reply is refused before it reaches the platform.</summary>
    public const string ReplyEmpty = "prComments.reply.empty";

    public const string ReplyDone = "prComments.reply.done";

    /// <summary>The reply did not post: <c>{0}</c> is the platform's own message.</summary>
    public const string ReplyFailed = "prComments.reply.failed";

    public const string ResolveDone = "prComments.resolve.done";

    /// <summary>Resolving a thread that is already resolved: an answer, not a second platform write.</summary>
    public const string ResolveAlready = "prComments.resolve.already";

    /// <summary>The resolve call failed: <c>{0}</c> is the platform's own message.</summary>
    public const string ResolveFailed = "prComments.resolve.failed";

    public const string HandOffRan = "prComments.handoff.ran";
    public const string HandOffQueued = "prComments.handoff.queued";
    public const string HandOffNone = "prComments.handoff.none";

    /// <summary>The label the handed-over message carries in the session's composer.</summary>
    public const string HandOffOrigin = "prComments.handoff.origin";

    /// <summary>An action threw instead of reporting a failed result: <c>{0}</c> is the exception message.</summary>
    public const string ActionFailed = "prComments.action.failed";

    [ModuleInitializer]
    internal static void Initialize() => Register();

    public static string NumberText(int number) => Loc.Instance.Format(Number, number);

    public static string ReadReadingText => Loc.Instance[ReadReading];

    public static string ReadFailedText(string reason) => Loc.Instance.Format(ReadFailed, reason);

    public static string ReadAtText(string clock) => Loc.Instance.Format(ReadAt, clock);

    /// <summary>"未解决 3", "已解决 1", "过期 2": the chip's label with the count behind it.</summary>
    public static string FilterText(CommentFilter filter, int count) => Loc.Instance.Format(
        filter switch
        {
            CommentFilter.Resolved => FilterResolved,
            CommentFilter.Outdated => FilterOutdated,
            _ => FilterOpen,
        },
        TextFormat.Number(count));

    public static string EmptyTitle(CommentFilter filter) => Loc.Instance[filter switch
    {
        CommentFilter.Resolved => EmptyResolved,
        CommentFilter.Outdated => EmptyOutdated,
        _ => EmptyOpen,
    }];

    public static string EmptyBodyText => Loc.Instance[EmptyBody];

    /// <summary>"src/main.cs:42" — or the line the thread was first placed on once the diff moved past it.</summary>
    public static string LocationText(string path, int line, bool original) =>
        Loc.Instance.Format(original ? LocationOriginal : Location, path, TextFormat.Id(line));

    public static string LocationUnknownText => Loc.Instance[LocationUnknown];

    public static string ResolvedText => Loc.Instance[Resolved];

    public static string OutdatedText => Loc.Instance[Outdated];

    public static string ResolvedByText(string who) => Loc.Instance.Format(ResolvedBy, who);

    public static string CommentCountText(int count) =>
        Loc.Instance.Format(CommentCount, TextFormat.Number(count));

    public static string AnonymousText => Loc.Instance[Anonymous];

    public static string ButtonReplyText => Loc.Instance[ButtonReply];

    /// <summary>The row's one state action reads 解决 or 重开 depending on where the thread is.</summary>
    public static string ButtonResolveText(bool resolved) => Loc.Instance[resolved ? ButtonReopen : ButtonResolve];

    public static string ReopenUnavailableText => Loc.Instance[ReopenUnavailable];

    public static string ButtonHandOffText => Loc.Instance[ButtonHandOff];

    public static string ReplyPlaceholderText => Loc.Instance[ReplyPlaceholder];

    public static string ReplyEmptyText => Loc.Instance[ReplyEmpty];

    public static string ReplyDoneText => Loc.Instance[ReplyDone];

    public static string ReplyFailedText(string reason) => Loc.Instance.Format(ReplyFailed, reason);

    public static string ResolveDoneText => Loc.Instance[ResolveDone];

    public static string ResolveAlreadyText => Loc.Instance[ResolveAlready];

    public static string ResolveFailedText(string reason) => Loc.Instance.Format(ResolveFailed, reason);

    public static string HandOffRanText => Loc.Instance[HandOffRan];

    public static string HandOffQueuedText => Loc.Instance[HandOffQueued];

    public static string HandOffNoneText => Loc.Instance[HandOffNone];

    public static string HandOffOriginText => Loc.Instance[HandOffOrigin];

    public static string ActionFailedText(string reason) => Loc.Instance.Format(ActionFailed, reason);

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(Title, "PR 评论", "Review comments");
        Loc.Register(Number, "#{0}", "#{0}");
        Loc.Register(ButtonRefresh, "刷新", "Refresh");
        Loc.Register(ButtonClose, "关闭", "Close");
        Loc.Register(ButtonOpenPullRequest, "在浏览器打开", "Open in browser");
        Loc.Register(ReadReading, "正在读取…", "Reading…");
        Loc.Register(ReadFailed, "读取失败:{0}", "Could not read the threads: {0}");
        Loc.Register(ReadAt, "读取于 {0}", "read at {0}");

        Loc.Register(FilterOpen, "未解决 {0}", "open {0}");
        Loc.Register(FilterResolved, "已解决 {0}", "resolved {0}");
        Loc.Register(FilterOutdated, "过期 {0}", "outdated {0}");
        Loc.Register(EmptyOpen, "没有未解决的评论线程", "No unresolved review threads");
        Loc.Register(EmptyResolved, "没有已解决的评论线程", "No resolved review threads");
        Loc.Register(EmptyOutdated, "没有过期的评论线程", "No outdated review threads");
        Loc.Register(
            EmptyBody,
            "「刷新」会重新读取平台上的线程。",
            "Refresh reads the platform's threads again.");

        Loc.Register(Location, "{0}:{1}", "{0}:{1}");
        Loc.Register(LocationOriginal, "{0}:{1}(原行)", "{0}:{1} (original line)");
        Loc.Register(LocationUnknown, "位置未知", "Location unknown");
        Loc.Register(Resolved, "已解决", "resolved");
        Loc.Register(Outdated, "已过期", "outdated");
        Loc.Register(ResolvedBy, "由 {0} 解决", "resolved by {0}");
        Loc.Register(CommentCount, "{0} 条评论", "{0} comments");
        Loc.Register(Anonymous, "匿名", "Anonymous");

        Loc.Register(ButtonReply, "回复", "Reply");
        Loc.Register(ButtonResolve, "解决", "Resolve");
        Loc.Register(ButtonReopen, "重开", "Reopen");
        Loc.Register(
            ReopenUnavailable,
            "平台接口没有「重开」:这里只能解决线程",
            "The platform API has no reopen; a thread can only be resolved here");
        Loc.Register(ButtonHandOff, "让 agent 处理", "Have the agent handle it");
        Loc.Register(ReplyPlaceholder, "写下回复…", "Write a reply…");
        Loc.Register(ReplyEmpty, "回复不能为空", "A reply cannot be empty");
        Loc.Register(ReplyDone, "已回复", "Replied");
        Loc.Register(ReplyFailed, "回复失败:{0}", "Could not reply: {0}");
        Loc.Register(ResolveDone, "已解决", "Resolved");
        Loc.Register(ResolveAlready, "这个线程已经解决", "This thread is already resolved");
        Loc.Register(ResolveFailed, "解决失败:{0}", "Could not resolve it: {0}");
        Loc.Register(HandOffRan, "已交给当前会话的 agent", "Handed to the agent of the open session");
        Loc.Register(HandOffQueued, "会话正忙,提示已放进输入框", "The session is busy; the prompt is in its composer");
        Loc.Register(HandOffNone, "没有可交付的会话", "There is no open session to hand it to");
        Loc.Register(HandOffOrigin, "PR 评论", "Pull request comments");
        Loc.Register(ActionFailed, "操作失败:{0}", "It did not work: {0}");
    }
}