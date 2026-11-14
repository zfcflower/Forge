using System.Collections.Generic;
using System.Numerics;
using Avalonia.Media;

namespace Forge.App.ViewModels;

/// <summary>
/// The tree icons, as geometry so the shell ships no icon package and no font dependency. Every glyph is the 24 px
/// regular variant of the same Fluent System Icon the chrome uses (see <c>Resources/Icons.axaml</c>); the view paints it
/// with the built-in <c>PathIcon</c>, which fills with <c>Foreground</c>. Geometries are thread-affine Avalonia objects,
/// so they are cached per thread: each UI thread (and each headless test session) gets its own set.
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

    public static Geometry General => Project;

    public static Geometry Project => Set.Project;

    /// <summary>The trailing "+" of a row that starts a session: a plain plus.</summary>
    public static Geometry Plus => Set.Plus;

    /// <summary>The trailing "+" of a project row, which starts a worktree branch instead of a session.</summary>
    public static Geometry NewBranch => Set.NewBranch;

    public static Geometry Worktree => Set.Worktree;

    /// <summary>
    /// The repository root checkout: Fluent <c>home_garage</c>, a house with a garage door. A worktree row wears the branch
    /// glyph, so the project's own checkout needs a shape of its own rather than a second worktree branch.
    /// </summary>
    public static Geometry MainCheckout => Set.MainCheckout;

    public static Geometry History => Set.History;

    public static Geometry Session => Set.Session;

    public static Geometry Bubble => Set.Bubble;

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
        /// <summary>展开箭头:与 <c>TreeChevronGeometry</c> 同一枚 Fluent chevron_right;展开时由视图把整个图标转 90°。</summary>
        public Geometry Chevron { get; } = Parse("M8.47 4.22c-.3.3-.3.77 0 1.06L15.19 12l-6.72 6.72a.75.75 0 1 0 1.06 1.06l7.25-7.25c.3-.3.3-.77 0-1.06L9.53 4.22a.75.75 0 0 0-1.06 0");

        public Geometry Folder { get; } = Parse("M3.5 6.25V8h4.63q.31 0 .53-.22l1.53-1.53-1.53-1.53a.8.8 0 0 0-.53-.22H5.25c-.97 0-1.75.78-1.75 1.75m-1.5 0C2 4.45 3.46 3 5.25 3h2.88c.6 0 1.17.24 1.59.66l1.84 1.84h7.19c1.8 0 3.25 1.46 3.25 3.25v9c0 1.8-1.46 3.25-3.25 3.25H5.25A3.25 3.25 0 0 1 2 17.75zM3.5 9.5v8.25c0 .97.78 1.75 1.75 1.75h13.5c.97 0 1.75-.78 1.75-1.75v-9c0-.97-.78-1.75-1.75-1.75h-7.19L9.72 8.84c-.42.42-1 .66-1.6.66z");

        public Geometry FolderOpen { get; } = Parse("M3.5 6.25c0-.97.78-1.75 1.75-1.75h2.88q.31 0 .53.22l2.06 2.06q.22.22.53.22h5.5c.97 0 1.75.78 1.75 1.75q0 .13.04.25H8.72c-1.34 0-2.58.71-3.25 1.87L3.5 14.28zM2 17.79A3.25 3.25 0 0 0 5.25 21h11.04c1.33 0 2.57-.72 3.24-1.88l3.03-5.25A3.25 3.25 0 0 0 19.96 9a1 1 0 0 0 .04-.25c0-1.8-1.45-3.25-3.25-3.25h-5.19L9.72 3.66c-.42-.42-1-.66-1.6-.66H5.26A3.25 3.25 0 0 0 2 6.25v11.54m6.72-7.3h11.03a1.75 1.75 0 0 1 1.51 2.63l-3.03 5.25c-.4.7-1.14 1.13-1.95 1.13H5.25a1.75 1.75 0 0 1-1.51-2.63l3.03-5.25c.4-.7 1.14-1.12 1.95-1.12");

        public Geometry File { get; } = Parse("M6 2a2 2 0 0 0-2 2v16c0 1.1.9 2 2 2h12a2 2 0 0 0 2-2V9.83a2 2 0 0 0-.59-1.42L13.6 2.6A2 2 0 0 0 12.17 2zm-.5 2c0-.28.22-.5.5-.5h6V8c0 1.1.9 2 2 2h4.5v10a.5.5 0 0 1-.5.5H6a.5.5 0 0 1-.5-.5zm11.88 4.5H14a.5.5 0 0 1-.5-.5V4.62z");

        public Geometry General => Project;

        /// <summary>
        /// The one container glyph of the tree, worn by both the general section and every project: an open box. It reads as
        /// "an area that holds conversations" without repeating the file tree's folder, which in this panel would say
        /// "a directory" instead.
        /// </summary>
        public Geometry Project { get; } = Parse("M6.05 7.98a.75.75 0 0 1 .97-.43L12 9.44l4.98-1.9a.75.75 0 1 1 .54 1.41l-4.77 1.8v5.5a.75.75 0 1 1-1.5 0v-5.5l-4.77-1.8a.75.75 0 0 1-.43-.97m4.54-5.47c.9-.37 1.92-.37 2.82 0l7.5 3.04c.66.27 1.09.91 1.09 1.62v9.66c0 .7-.43 1.35-1.1 1.62l-7.5 3.04c-.9.36-1.9.36-2.8 0l-7.5-3.04c-.67-.27-1.1-.91-1.1-1.62V7.17c0-.71.43-1.35 1.1-1.62zm2.26 1.4a2.3 2.3 0 0 0-1.7 0l-7.5 3.03a.3.3 0 0 0-.15.23v9.66q0 .16.16.23l7.5 3.04c.54.22 1.14.22 1.69 0l7.5-3.04q.14-.07.15-.23V7.17q0-.17-.16-.23z");

        public Geometry Plus { get; } = Parse("M12 3.25c.41 0 .75.34.75.75v7.25H20a.75.75 0 0 1 0 1.5h-7.25V20a.75.75 0 0 1-1.5 0v-7.25H4a.75.75 0 0 1 0-1.5h7.25V4c0-.41.34-.75.75-.75");

        /// <summary>
        /// A branch with a plus: the project row's "+" starts a worktree on a new branch, while a session "+" (the plain
        /// plus) only starts another conversation.
        /// </summary>
        public Geometry NewBranch { get; } = Parse("M4 5.5a3.5 3.5 0 1 1 4.49 3.36 5.5 5.5 0 0 0 5.26 3.89h.33a3.5 3.5 0 1 1 0 1.5h-.33a7 7 0 0 1-5.5-2.67v3.5a3.5 3.5 0 1 1-1.5 0V8.92A3.5 3.5 0 0 1 4 5.5m3.5-2a2 2 0 1 0 0 4 2 2 0 0 0 0-4m0 13a2 2 0 1 0 0 4 2 2 0 0 0 0-4m8-3a2 2 0 1 0 4 0 2 2 0 0 0-4 0");

        public Geometry Worktree { get; } = Parse("M11 5.5a3.5 3.5 0 0 1-3 3.46v2.54h6.25c1.24 0 2.25-1 2.25-2.25v-.4a3.5 3.5 0 1 1 1.5.11v.29A3.75 3.75 0 0 1 14.25 13H8v2.04a3.5 3.5 0 1 1-1.5.1V8.86A3.5 3.5 0 1 1 11 5.5m-3.5 2a2 2 0 1 0 0-4 2 2 0 0 0 0 4m10 0a2 2 0 1 0 0-4 2 2 0 0 0 0 4m-8 11a2 2 0 1 0-4 0 2 2 0 0 0 4 0");

        /// <summary>The repository root checkout: the same Fluent <c>home_garage</c> shape the icon table carries.</summary>
        public Geometry MainCheckout { get; } = Parse("M11.51 3.68 4.76 9.4a.8.8 0 0 0-.26.57v9.27c0 .14.11.25.25.25H7v-5.25c0-.97.78-1.75 1.75-1.75h6.5c.97 0 1.75.78 1.75 1.75v5.25h2.25q.23-.02.25-.25V9.98q-.01-.35-.26-.57l-6.75-5.73a.75.75 0 0 0-.98 0M8.5 17.5v2h7v-2zm7-1.5v-1.75a.25.25 0 0 0-.25-.25h-6.5a.25.25 0 0 0-.25.25V16zM4.75 21C3.78 21 3 20.22 3 19.25V9.98c0-.66.29-1.3.8-1.72l6.74-5.73a2.25 2.25 0 0 1 2.92 0l6.75 5.73c.5.43.79 1.06.79 1.72v9.27c0 .97-.78 1.75-1.75 1.75z");

        public Geometry History { get; } = Parse("M12 4.5a7.5 7.5 0 1 1-7.42 6.4c.07-.46-.26-.9-.72-.9-.37 0-.7.26-.76.62Q3 11.3 3 12a9 9 0 1 0 3-6.7V4.24a.75.75 0 0 0-1.5 0v3c0 .41.34.75.75.75h3a.75.75 0 0 0 0-1.5H6.9a7.5 7.5 0 0 1 5.1-2m.5 3.25a.75.75 0 0 0-1.5 0v4.5c0 .41.34.75.75.75h3a.75.75 0 0 0 0-1.5H12.5z");

        public Geometry Session => Bubble;

        /// <summary>
        /// The one conversation glyph of the tree — Fluent <c>chat_sparkle</c>, the same one the tab strip's <c>IconChat</c> wears: a session row and an agent
        /// row are both conversations. The active session's row is told apart by colour, not by a second, filled variant.
        /// </summary>
        public Geometry Bubble { get; } = Parse("M16.09 6.41a3 3 0 0 0-1.35-.95L13.36 5a.54.54 0 0 1 0-1.03l1.38-.44a2.8 2.8 0 0 0 1.76-1.77l.01-.03.45-1.38a.54.54 0 0 1 1.03 0l.44 1.38a2.8 2.8 0 0 0 1.8 1.8l1.38.44.03.01a.54.54 0 0 1 0 1.03l-1.38.44a2.8 2.8 0 0 0-1.8 1.8l-.45 1.38-.01.03a.54.54 0 0 1-1.01-.03l-.45-1.38q-.15-.46-.45-.85m7.7 3.8-.77-.25a1.6 1.6 0 0 1-1-1l-.25-.76a.3.3 0 0 0-.57 0l-.25.77a1.6 1.6 0 0 1-.98 1l-.77.24a.3.3 0 0 0 0 .57l.77.25a1.6 1.6 0 0 1 1 1l.24.77a.3.3 0 0 0 .58 0l.24-.77a1.6 1.6 0 0 1 1-1l.77-.24a.3.3 0 0 0 0-.57zM12 2a10 10 0 0 1 2.76.39q-.18.1-.37.17L13.05 3q-.43.16-.72.5H12a8.5 8.5 0 0 0-7.43 12.64l.15.27-1.1 3.98 3.98-1.11.27.15a8.5 8.5 0 0 0 12.5-5.96q.14.2.34.34a1.3 1.3 0 0 0 1.09.2A10 10 0 0 1 7.4 20.88L3.6 21.96a1.25 1.25 0 0 1-1.54-1.54l1.06-3.83A10 10 0 0 1 12 2");

        public Geometry MainAgent => Bubble;

        public Geometry Agent => Bubble;

        /// <summary>Scout: a magnifier — the sub-agent that only reads.</summary>
        public Geometry Scout { get; } = Parse("M16.1 17.16a8 8 0 1 1 1.06-1.06l4.62 4.62a.75.75 0 1 1-1.06 1.06zM17.5 11a6.5 6.5 0 1 0-13 0 6.5 6.5 0 0 0 13 0");

        /// <summary>Worker: the angle brackets of code.</summary>
        public Geometry Worker { get; } = Parse("M15.05 1.5a.75.75 0 0 1 1.4.5l-7.5 20.5a.75.75 0 0 1-1.4-.5zM6.2 6.22a.75.75 0 0 1 1.08 1.04L2.8 12l4.5 4.73a.75.75 0 0 1-1.08 1.04l-5-5.25a.75.75 0 0 1 0-1.04zm10.52-.02a.75.75 0 0 1 1.06.02l5 5.25c.28.3.28.75 0 1.04l-5 5.25a.75.75 0 0 1-1.08-1.04L21.2 12l-4.5-4.73a.75.75 0 0 1 .02-1.06");

        /// <summary>Planner: a bulleted list.</summary>
        public Geometry Planner { get; } = Parse("M3.25 17.5a1.25 1.25 0 1 1 0 2.5 1.25 1.25 0 0 1 0-2.5m3.5.5h14.5a.75.75 0 0 1 .1 1.5H6.75a.75.75 0 0 1-.1-1.5zh14.5zm-3.5-7a1.25 1.25 0 1 1 0 2.5 1.25 1.25 0 0 1 0-2.5m3.5.5h14.5a.75.75 0 0 1 .1 1.5H6.75a.75.75 0 0 1-.1-1.5zh14.5zm-3.5-7a1.25 1.25 0 1 1 0 2.5 1.25 1.25 0 0 1 0-2.5m3.5.5h14.5a.75.75 0 0 1 .1 1.5H6.75a.75.75 0 0 1-.1-1.5zh14.5z");

        /// <summary>Reviewer: a check mark.</summary>
        public Geometry Reviewer { get; } = Parse("M4.53 12.97a.75.75 0 0 0-1.06 1.06l4.5 4.5c.3.3.77.3 1.06 0l11-11a.75.75 0 0 0-1.06-1.06L8.5 16.94z");

        private static Geometry Parse(string path) => StreamGeometry.Parse(path);
    }
}

/// <summary>Global motion gate for looping animations - the tree's running-agent breathing and the chat timeline's running
/// node. Avalonia 12 exposes no OS reduced-motion preference and this module forbids P/Invoke, so the shell sets this from
/// the appearance preference (<see cref="Forge.App.Appearance.AppearanceApplier"/>).</summary>
internal static class TreeMotion
{
    private static bool reducedMotion;

    /// <summary>True when looping tree animations (the running agent's breathing) must not play.</summary>
    public static bool ReducedMotion
    {
        get => reducedMotion;
        set
        {
            if (reducedMotion == value)
            {
                return;
            }

            reducedMotion = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Raised on the UI thread when <see cref="ReducedMotion"/> changed, so a live looping animation (a running timeline
    /// node) can stop at once instead of on its next unrelated notification.
    /// </summary>
    public static event EventHandler? Changed;
}

/// <summary>One indent step of a tree row; <see cref="DrawLine"/> is the 1 px guide line of a tree, not of an agent list,
/// and <see cref="Step"/> is the cell width the view gives this mark.</summary>
internal sealed record IndentMark(int Level, bool DrawLine, int Step);

/// <summary>Shared indent lists, so a row references one cached array instead of allocating its own per render.</summary>
internal static class IndentMarks
{
    /// <summary>Width of the chevron column; the chevron box fills it, so a child chevron lines up under its parent's icon.</summary>
    public const int ChevronSlot = 10;

    /// <summary>The panel padding every row is inset by, so a row does not start against the panel edge.</summary>
    public const int RowInset = 6;

    /// <summary>Gap between the chevron column and the icon; the row templates use the same value as the icon's left margin.</summary>
    public const int IconGap = 6;

    /// <summary>One indent step for folder/file rows: the chevron slot plus the chevron-to-icon gap.</summary>
    public const int Step = ChevronSlot + IconGap;

    /// <summary>
    /// The whole leading width of a row before its icon: the panel inset, the chevron column (which an agent row leaves
    /// empty) and the icon gap. Both row templates indent by this plus <see cref="Step"/> per level, so an agent row lands
    /// under the row it belongs to instead of level with it.
    /// </summary>
    public const int IconIndent = RowInset + ChevronSlot + IconGap;

    /// <summary>One indent step for agent rows, which keep the previous 16 px rhythm and draw no guide lines.</summary>
    public const int SpacerStep = 16;

    private const int MaxDepth = 32;

    private static readonly IReadOnlyList<IndentMark>[] Lines = Build(drawLine: true, Step);
    private static readonly IReadOnlyList<IndentMark>[] PlainSteps = Build(drawLine: false, Step);
    private static readonly IReadOnlyList<IndentMark>[] Spacers = Build(drawLine: false, SpacerStep);
    private static readonly Dictionary<int, IReadOnlyList<IndentMark>> Masks = [];

    /// <summary>Indent steps joined by a vertical guide line, for a fully-expanded chain.</summary>
    public static IReadOnlyList<IndentMark> For(int depth) => At(Lines, depth);

    /// <summary>Indent steps without a guide line, for the agent tree.</summary>
    public static IReadOnlyList<IndentMark> Spacer(int depth) => At(Spacers, depth);

    /// <summary>
    /// Indent steps of the folder width that draw no line: the project tree keeps the depth rhythm but no longer threads a
    /// vertical line through it, since a hairline between rows read as a second, competing column.
    /// </summary>
    public static IReadOnlyList<IndentMark> Plain(int depth) => At(PlainSteps, depth);

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
