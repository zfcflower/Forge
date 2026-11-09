using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The scope and base-branch pickers of the files panel's changed-files tab, plus its tree toggle. Keys
/// <c>changes.picker.*</c>; typed accessors for the texts that take arguments.
/// </summary>
internal static class ChangesPickerStrings
{
    public const string UncommittedNone = "changes.picker.uncommittedNone";
    public const string LatestTurn = "changes.picker.latestTurn";
    public const string LatestTurnShortcutPattern = "changes.picker.latestTurnShortcut";
    public const string TurnTitlePattern = "changes.picker.turnTitle";
    public const string SubtitlePattern = "changes.picker.subtitle";
    public const string TurnsGroup = "changes.picker.turns";
    public const string CommitsGroup = "changes.picker.commits";
    public const string NoCommits = "changes.picker.noCommits";
    public const string RemoteGroup = "changes.picker.remote";
    public const string LocalGroup = "changes.picker.local";
    public const string DefaultTag = "changes.picker.default";
    public const string SearchWatermark = "changes.picker.search";
    public const string SearchName = "changes.picker.search.name";
    public const string NoMatch = "changes.picker.noMatch";
    public const string ReloadBranches = "changes.picker.reload";
    public const string CollapseAll = "changes.picker.collapseAll";
    public const string ExpandAll = "changes.picker.expandAll";
    public const string AgoNow = "changes.picker.ago.now";
    public const string AgoMinutesBase = "changes.picker.ago.minutes";
    public const string AgoHoursBase = "changes.picker.ago.hours";
    public const string AgoDaysBase = "changes.picker.ago.days";
    public const string AgoMonthsBase = "changes.picker.ago.months";
    public const string AgoYearsBase = "changes.picker.ago.years";

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(UncommittedNone, "没有未提交的改动", "No uncommitted changes");
        Loc.Register(LatestTurn, "上一轮", "Last turn");
        Loc.Register(LatestTurnShortcutPattern, "上一轮 · {0}", "Last turn · {0}");
        Loc.Register(TurnTitlePattern, "第 {0} 轮", "Turn at {0}");
        Loc.Register(SubtitlePattern, "{0} · {1}", "{0} · {1}");
        Loc.Register(TurnsGroup, "AGENT 轮次", "AGENT TURNS");
        Loc.Register(CommitsGroup, "COMMIT", "COMMITS");
        Loc.Register(NoCommits, "此分支上没有 commit", "No commits on this branch");
        Loc.Register(RemoteGroup, "远程", "Remote");
        Loc.Register(LocalGroup, "本地", "Local");
        Loc.Register(DefaultTag, "默认", "default");
        Loc.Register(SearchWatermark, "搜索分支…", "Search branches…");
        Loc.Register(SearchName, "搜索分支", "Search branches");
        Loc.Register(NoMatch, "没有匹配的分支", "No matching branch");
        Loc.Register(ReloadBranches, "刷新分支", "Refresh branches");
        Loc.Register(CollapseAll, "全部折叠", "Collapse all");
        Loc.Register(ExpandAll, "全部展开", "Expand all");
        Loc.Register(AgoNow, "刚刚", "just now");
        Loc.Register(AgoMinutesBase + ".one", "{0} 分钟前", "{0} minute ago");
        Loc.Register(AgoMinutesBase + ".other", "{0} 分钟前", "{0} minutes ago");
        Loc.Register(AgoHoursBase + ".one", "{0} 小时前", "{0} hour ago");
        Loc.Register(AgoHoursBase + ".other", "{0} 小时前", "{0} hours ago");
        Loc.Register(AgoDaysBase + ".one", "{0} 天前", "{0} day ago");
        Loc.Register(AgoDaysBase + ".other", "{0} 天前", "{0} days ago");
        Loc.Register(AgoMonthsBase + ".one", "{0} 个月前", "{0} month ago");
        Loc.Register(AgoMonthsBase + ".other", "{0} 个月前", "{0} months ago");
        Loc.Register(AgoYearsBase + ".one", "{0} 年前", "{0} year ago");
        Loc.Register(AgoYearsBase + ".other", "{0} 年前", "{0} years ago");
    }

    /// <summary>"第 14:05 轮": a turn is named by when it started.</summary>
    public static string TurnTitle(DateTimeOffset startedAt) =>
        Loc.Instance.Format(TurnTitlePattern, startedAt.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>"上一轮 · 3 个文件", or the bare "上一轮" while the file count is not known yet.</summary>
    public static string LatestTurnShortcut(int? fileCount) =>
        fileCount is { } count
            ? Loc.Instance.Format(LatestTurnShortcutPattern, FileCount(count))
            : Loc.Instance[LatestTurn];

    /// <summary>"3 个文件" (shares the changes summary's plural forms).</summary>
    public static string FileCount(int count) =>
        LocText.Plural(InspectorStrings.ChangesSummaryFilesBase, count, TextFormat.Number(count)).Resolve();

    /// <summary>"3 个文件 · 3 天前", or only the age while the count is unknown.</summary>
    public static string TurnSubtitle(int? fileCount, DateTimeOffset at, DateTimeOffset now) =>
        fileCount is { } count
            ? Loc.Instance.Format(SubtitlePattern, FileCount(count), RelativeTime(at, now))
            : RelativeTime(at, now);

    /// <summary>"abc1234 · 3 天前".</summary>
    public static string CommitSubtitle(string shortSha, DateTimeOffset at, DateTimeOffset now) =>
        Loc.Instance.Format(SubtitlePattern, shortSha, RelativeTime(at, now));

    /// <summary>The age of <paramref name="at"/> as of <paramref name="now"/>, in the largest whole unit.</summary>
    public static string RelativeTime(DateTimeOffset at, DateTimeOffset now)
    {
        var elapsed = now - at;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return Loc.Instance[AgoNow];
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return Ago(AgoMinutesBase, (long)elapsed.TotalMinutes);
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return Ago(AgoHoursBase, (long)elapsed.TotalHours);
        }

        var days = (long)elapsed.TotalDays;
        if (days < 30)
        {
            return Ago(AgoDaysBase, days);
        }

        return days < 365 ? Ago(AgoMonthsBase, days / 30) : Ago(AgoYearsBase, days / 365);
    }

    private static string Ago(string keyBase, long count) =>
        LocText.Plural(keyBase, count, TextFormat.Id(count)).Resolve();
}
