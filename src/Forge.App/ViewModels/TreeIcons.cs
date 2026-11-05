using Avalonia.Media;

namespace Forge.App.ViewModels;

/// <summary>
/// The tree icons, as geometry so the shell ships no icon package and no font dependency. Icons are filled paths on a
/// 24×24 box and are scaled by the view.
/// </summary>
internal static class TreeIcons
{
    public static Geometry Chevron { get; } = Parse("M9 4.5 17 12 9 19.5 9 16 12.5 12 9 8Z");

    public static Geometry Folder { get; } = Parse("M2 6h6.6l2.2 3.2H22V19H2Z");

    public static Geometry File { get; } = Parse(
        "M6 2h7.5L20 8.5V21a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V3a1 1 0 0 1 1-1Zm7 1.6V9h5.4Z");

    public static Geometry General { get; } = Parse("M12 3 21 12 12 21 3 12Z");

    public static Geometry Project { get; } = Parse("M3 4h18v4H3Zm0 6h18v10H3Zm3 3v4h5v-4Z");

    public static Geometry Worktree { get; } = Parse("M6 3a2.5 2.5 0 1 1 0 5 2.5 2.5 0 0 1 0-5Zm12 0a2.5 2.5 0 1 1 0 5 2.5 2.5 0 0 1 0-5Z"
        + "M6 8v6.5a3.5 3.5 0 0 0 3.5 3.5H12v2.5l4.5-4L12 12.5V15H9.5A1.5 1.5 0 0 1 8 13.5V8Z");

    public static Geometry History { get; } = Parse("M12 3a9 9 0 1 0 8.4 12h-2.2A7 7 0 1 1 12 5c1.9 0 3.5.7 4.8 2H13v2h7V2h-2v2.6A8.9 8.9 0 0 0 12 3Z"
        + "M11 7v6.4l5 2.6 1-1.7-4-2.1V7Z");

    public static Geometry Session { get; } = Parse("M4 4h16v11H8l-4 4Zm3 3v2h10V7Zm0 3.5v2h7v-2Z");

    public static Geometry MainAgent { get; } = Parse("M12 2 14.6 9.4 22 12 14.6 14.6 12 22 9.4 14.6 2 12 9.4 9.4Z");

    public static Geometry Agent { get; } = Parse("M12 4 20 12 12 20 4 12Z", "M12 8.5 15.5 12 12 15.5 8.5 12Z");

    public static Geometry Scout { get; } = Parse(
        "M10.5 3a7.5 7.5 0 1 0 0 15 7.5 7.5 0 0 0 0-15Zm0 2.6a4.9 4.9 0 1 1 0 9.8 4.9 4.9 0 0 1 0-9.8Z"
        + "M15.6 16.9 21 22.3 22.3 21 16.9 15.6Z");

    public static Geometry Worker { get; } = Parse("M3 17.6 13.6 7l3.4 3.4L6.4 21Z M15 5.6 18.4 2.2 21.8 5.6 18.4 9Z");

    public static Geometry Planner { get; } = Parse("M4 4h16v3.2H4Zm0 6.4h11V13.6H4Zm0 6.4h16V20H4Z");

    public static Geometry Reviewer { get; } = Parse("M9.4 20 2.6 13.2 5.4 10.4 9.4 14.4 18.6 5.2 21.4 8Z");

    public static Geometry IconOf(string? agentKind) => agentKind switch
    {
        null or "" => Agent,
        "Scout" => Scout,
        "Worker" => Worker,
        "Plan" => Planner,
        "Reviewer" => Reviewer,
        _ => Agent,
    };

    private static Geometry Parse(string path) => Geometry.Parse(path);

    private static Geometry Parse(string outer, string hole) =>
        new CombinedGeometry(GeometryCombineMode.Exclude, Geometry.Parse(outer), Geometry.Parse(hole));
}

/// <summary>One 12 px indent step of a tree row; <see cref="DrawLine"/> is the 1 px guide line of a tree, not of an agent list.</summary>
internal sealed record IndentMark(int Level, bool DrawLine);

/// <summary>Shared per-depth indent lists, so a row only references one array instead of allocating its own.</summary>
internal static class IndentMarks
{
    private const int MaxDepth = 32;

    private static readonly IReadOnlyList<IndentMark>[] Lines = Build(drawLine: true);
    private static readonly IReadOnlyList<IndentMark>[] Spacers = Build(drawLine: false);

    /// <summary>Indent steps joined by a vertical guide line, for the project and file trees.</summary>
    public static IReadOnlyList<IndentMark> For(int depth) => At(Lines, depth);

    /// <summary>Indent steps without a guide line, for the agent tree.</summary>
    public static IReadOnlyList<IndentMark> Spacer(int depth) => At(Spacers, depth);

    private static IReadOnlyList<IndentMark> At(IReadOnlyList<IndentMark>[] cache, int depth) =>
        depth <= 0 ? [] : cache[depth < MaxDepth ? depth : MaxDepth - 1];

    private static IReadOnlyList<IndentMark>[] Build(bool drawLine)
    {
        var cache = new IReadOnlyList<IndentMark>[MaxDepth];
        cache[0] = [];
        for (var depth = 1; depth < MaxDepth; depth++)
        {
            var marks = new IndentMark[depth];
            for (var level = 0; level < depth; level++)
            {
                marks[level] = new IndentMark(level, drawLine);
            }

            cache[depth] = marks;
        }

        return cache;
    }
}
