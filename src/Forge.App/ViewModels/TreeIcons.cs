using System.Collections.Generic;
using System.Numerics;
using Avalonia.Media;

namespace Forge.App.ViewModels;

/// <summary>
/// The tree icons, as geometry so the shell ships no icon package and no font dependency. Icons are stroked outlines on
/// a 24×24 box and are scaled by the view; the view paints them with <c>Stroke</c> (no fill). Geometries are thread-affine
/// Avalonia objects, so they are cached per thread: each UI thread (and each headless test session) gets its own set.
/// </summary>
internal static class TreeIcons
{
    [ThreadStatic]
    private static TreeIconSet? set;

    private static TreeIconSet Set => set ??= new TreeIconSet();

    public static Geometry Chevron => Set.Chevron;

    /// <summary>Closed folder: a tabbed rectangle outline.</summary>
    public static Geometry Folder => Set.Folder;

    /// <summary>Open folder: the front flap is folded forward.</summary>
    public static Geometry FolderOpen => Set.FolderOpen;

    /// <summary>File: a sheet outline with its top-right corner folded.</summary>
    public static Geometry File => Set.File;

    public static Geometry General => Set.General;

    public static Geometry Project => Set.Project;

    public static Geometry Worktree => Set.Worktree;

    public static Geometry History => Set.History;

    public static Geometry Session => Set.Session;

    public static Geometry MainAgent => Set.MainAgent;

    public static Geometry Agent => Set.Agent;

    public static Geometry Scout => Set.Scout;

    public static Geometry Worker => Set.Worker;

    public static Geometry Planner => Set.Planner;

    public static Geometry Reviewer => Set.Reviewer;

    public static Geometry IconOf(string? agentKind) => agentKind switch
    {
        null or "" => Set.Agent,
        "Scout" => Set.Scout,
        "Worker" => Set.Worker,
        "Plan" => Set.Planner,
        "Reviewer" => Set.Reviewer,
        _ => Set.Agent,
    };

    private sealed class TreeIconSet
    {
        public Geometry Chevron { get; } = Parse("M9 4.5 17 12 9 19.5 9 16 12.5 12 9 8Z");

        public Geometry Folder { get; } = Parse(
            "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z");

        public Geometry FolderOpen { get; } = Parse(
            "m6 14 1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2");

        public Geometry File { get; } = Parse(
            "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z M14 2v4a2 2 0 0 0 2 2h4");

        public Geometry General { get; } = Parse("M12 3 21 12 12 21 3 12Z");

        public Geometry Project { get; } = Parse("M3 5h18v14H3Z M6 9.5h8v6H6Z");

        public Geometry Worktree { get; } = Parse("M6 3a2.5 2.5 0 1 1 0 5 2.5 2.5 0 0 1 0-5Zm12 0a2.5 2.5 0 1 1 0 5 2.5 2.5 0 0 1 0-5Z"
            + "M6 8v6.5a3.5 3.5 0 0 0 3.5 3.5H12v2.5l4.5-4L12 12.5V15H9.5A1.5 1.5 0 0 1 8 13.5V8Z");

        public Geometry History { get; } = Parse("M12 3a9 9 0 1 0 8.4 12h-2.2A7 7 0 1 1 12 5c1.9 0 3.5.7 4.8 2H13v2h7V2h-2v2.6A8.9 8.9 0 0 0 12 3Z"
            + "M11 7v6.4l5 2.6 1-1.7-4-2.1V7Z");

        public Geometry Session { get; } = Parse("M4 4h16v11H8l-4 4Z");

        public Geometry MainAgent { get; } = Parse("M12 2 14.6 9.4 22 12 14.6 14.6 12 22 9.4 14.6 2 12 9.4 9.4Z");

        public Geometry Agent { get; } = Parse("M12 4 20 12 12 20 4 12Z M12 8.5 15.5 12 12 15.5 8.5 12Z");

        public Geometry Scout { get; } = Parse(
            "M10.5 3a7.5 7.5 0 1 0 0 15 7.5 7.5 0 0 0 0-15Zm0 2.6a4.9 4.9 0 1 1 0 9.8 4.9 4.9 0 0 1 0-9.8Z"
            + "M15.6 16.9 21 22.3 22.3 21 16.9 15.6Z");

        public Geometry Worker { get; } = Parse("M3 17.6 13.6 7l3.4 3.4L6.4 21Z M15 5.6 18.4 2.2 21.8 5.6 18.4 9Z");

        public Geometry Planner { get; } = Parse("M4 5h16 M4 12h11 M4 19h16");

        public Geometry Reviewer { get; } = Parse("M4 12.5 9.5 18 20 6.5");

        private static Geometry Parse(string path) => StreamGeometry.Parse(path);
    }
}

/// <summary>Global motion gate for tree animations. Avalonia 12 exposes no OS reduced-motion preference and this module
/// forbids P/Invoke, so the shell can set this once it reads the preference another way.</summary>
internal static class TreeMotion
{
    /// <summary>True when looping tree animations (the running agent's breathing) must not play.</summary>
    public static bool ReducedMotion { get; set; }
}

/// <summary>One indent step of a tree row; <see cref="DrawLine"/> is the 1 px guide line of a tree, not of an agent list,
/// and <see cref="Step"/> is the cell width the view gives this mark.</summary>
internal sealed record IndentMark(int Level, bool DrawLine, int Step);

/// <summary>Shared indent lists, so a row references one cached array instead of allocating its own per render.</summary>
internal static class IndentMarks
{
    /// <summary>Width of the chevron column; the chevron box fills it, so a child chevron lines up under its parent's icon.</summary>
    public const int ChevronSlot = 8;

    /// <summary>Gap between the chevron column and the icon, so a child icon lines up near its parent's text.</summary>
    public const int IconGap = 14;

    /// <summary>One indent step for folder/file rows: the chevron slot plus the chevron-to-icon gap.</summary>
    public const int Step = ChevronSlot + IconGap;

    /// <summary>One indent step for agent rows, which keep the previous 16 px rhythm and draw no guide lines.</summary>
    public const int SpacerStep = 16;

    private const int MaxDepth = 32;

    private static readonly IReadOnlyList<IndentMark>[] Lines = Build(drawLine: true, Step);
    private static readonly IReadOnlyList<IndentMark>[] Spacers = Build(drawLine: false, SpacerStep);
    private static readonly Dictionary<int, IReadOnlyList<IndentMark>> Masks = [];

    /// <summary>Indent steps joined by a vertical guide line, for a fully-expanded chain.</summary>
    public static IReadOnlyList<IndentMark> For(int depth) => At(Lines, depth);

    /// <summary>Indent steps without a guide line, for the agent tree.</summary>
    public static IReadOnlyList<IndentMark> Spacer(int depth) => At(Spacers, depth);

    /// <summary>
    /// Marks for a row whose expanded ancestors occupy the levels in <paramref name="mask"/> (bit L = level L). The result
    /// is cached so template bindings never allocate a new collection.
    /// </summary>
    public static IReadOnlyList<IndentMark> ForMask(int mask)
    {
        if (mask == 0)
        {
            return [];
        }

        if (!Masks.TryGetValue(mask, out var marks))
        {
            marks = BuildMask(mask);
            Masks[mask] = marks;
        }

        return marks;
    }

    private static IReadOnlyList<IndentMark> BuildMask(int mask)
    {
        var count = BitOperations.PopCount((uint)mask);

        // A fully-expanded chain is the common case: reuse the shared per-depth array.
        if (count < MaxDepth && mask == (1 << count) - 1)
        {
            return At(Lines, count);
        }

        var marks = new IndentMark[count];
        var index = 0;
        for (var level = 0; level < MaxDepth && index < count; level++)
        {
            if ((mask & (1 << level)) != 0)
            {
                marks[index++] = new IndentMark(level, true, Step);
            }
        }

        return marks;
    }

    private static IReadOnlyList<IndentMark> At(IReadOnlyList<IndentMark>[] cache, int depth) =>
        depth <= 0 ? [] : cache[depth < MaxDepth ? depth : MaxDepth - 1];

    private static IReadOnlyList<IndentMark>[] Build(bool drawLine, int step)
    {
        var cache = new IReadOnlyList<IndentMark>[MaxDepth];
        cache[0] = [];
        for (var depth = 1; depth < MaxDepth; depth++)
        {
            var marks = new IndentMark[depth];
            for (var level = 0; level < depth; level++)
            {
                marks[level] = new IndentMark(level, drawLine, step);
            }

            cache[depth] = marks;
        }

        return cache;
    }
}
