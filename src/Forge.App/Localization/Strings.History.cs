using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The composer's Chat History flyout: its button, the filter field, the two empty states and the two row actions. Keys are
/// <c>session.history.*</c>. The relative age a row shows lives here too, because it is a sentence and not a number.
/// </summary>
internal static class ComposerHistoryStrings
{
    public const string NameKey = "session.history.name";
    public const string SearchKey = "session.history.search";
    public const string EmptyKey = "session.history.empty";
    public const string NoMatchKey = "session.history.noMatch";
    public const string SaveAsInstructionKey = "session.history.saveAsInstruction";
    public const string LocateKey = "session.history.locate";
    public const string JustNowKey = "session.history.justNow";
    public const string MinutesAgoKey = "session.history.minutesAgo";
    public const string YesterdayKey = "session.history.yesterday";

    public static string Name => Loc.Instance[NameKey];

    public static string Search => Loc.Instance[SearchKey];

    public static string Empty => Loc.Instance[EmptyKey];

    public static string NoMatch => Loc.Instance[NoMatchKey];

    public static string SaveAsInstruction => Loc.Instance[SaveAsInstructionKey];

    public static string Locate => Loc.Instance[LocateKey];

    /// <summary>
    /// A message's age as the row shows it: "just now" inside a minute, minutes inside the hour, the wall clock for anything
    /// earlier the same day, "yesterday + clock", and the short date beyond that - so the newest rows read as an age and the
    /// old ones as a timestamp.
    /// </summary>
    public static string Relative(DateTimeOffset at, DateTimeOffset? now = null)
    {
        var local = at.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime();
        var age = today - local;
        if (age < TimeSpan.FromMinutes(1) && age > TimeSpan.FromMinutes(-1))
        {
            return Loc.Instance[JustNowKey];
        }

        if (local.Date == today.Date)
        {
            return age < TimeSpan.FromHours(1)
                ? Loc.Instance.Format(MinutesAgoKey, Math.Max(1, (int)age.TotalMinutes))
                : TextFormat.Clock(at)[..5];
        }

        return local.Date == today.Date.AddDays(-1)
            ? Loc.Instance.Format(YesterdayKey, TextFormat.Clock(at)[..5])
            : TextFormat.ShortStamp(at);
    }

    [ModuleInitializer]
    internal static void Initialize() => Register();

    internal static void Register()
    {
        Loc.Register(NameKey, "会话消息记录", "Chat history");
        Loc.Register(SearchKey, "搜索消息", "Search messages");
        Loc.Register(EmptyKey, "这个会话还没有消息", "No messages yet");
        Loc.Register(NoMatchKey, "没有匹配的消息", "No messages match");
        Loc.Register(SaveAsInstructionKey, "保存为指令", "Save as instruction");
        Loc.Register(LocateKey, "定位到消息", "Locate the message");
        Loc.Register(JustNowKey, "刚刚", "just now");
        Loc.Register(MinutesAgoKey, "{0} 分钟前", "{0} min ago");
        Loc.Register(YesterdayKey, "昨天 {0}", "Yesterday {0}");
    }
}
