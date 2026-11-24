using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The context-footprint block of the tools page: how much one tool result may put into the conversation before it is cut
/// or stored as an artifact. Every number here is a preference with a hard tool-side ceiling. Keys: <c>tools.limits.*</c>.
/// </summary>
internal static class ToolsLimitsStrings
{
    public const string SectionKey = "tools.limits.section";
    public const string BashCaptureKey = "tools.limits.bashCapture";
    public const string BashCaptureHintKey = "tools.limits.bashCaptureHint";
    public const string GrepResultsKey = "tools.limits.grepResults";
    public const string GrepResultsHintKey = "tools.limits.grepResultsHint";
    public const string IgnoredDirectoriesKey = "tools.limits.ignoredDirectories";
    public const string IgnoredDirectoriesHintKey = "tools.limits.ignoredDirectoriesHint";
    public const string SpillKey = "tools.limits.spill";
    public const string SpillHintKey = "tools.limits.spillHint";
    public const string PreviewKey = "tools.limits.preview";
    public const string PreviewHintKey = "tools.limits.previewHint";

    [ModuleInitializer]
    internal static void Initialize() => Register();

    public static string Section => Loc.Instance[SectionKey];

    public static string BashCapture => Loc.Instance[BashCaptureKey];

    public static string BashCaptureHint => Loc.Instance[BashCaptureHintKey];

    public static string GrepResults => Loc.Instance[GrepResultsKey];

    public static string GrepResultsHint => Loc.Instance[GrepResultsHintKey];

    public static string IgnoredDirectories => Loc.Instance[IgnoredDirectoriesKey];

    public static string IgnoredDirectoriesHint => Loc.Instance[IgnoredDirectoriesHintKey];

    public static string Spill => Loc.Instance[SpillKey];

    public static string SpillHint => Loc.Instance[SpillHintKey];

    public static string Preview => Loc.Instance[PreviewKey];

    public static string PreviewHint => Loc.Instance[PreviewHintKey];

    internal static void Register()
    {
        Loc.Register(SectionKey, "上下文占用", "Context footprint");
        Loc.Register(
            BashCaptureKey,
            "bash 捕获上限（KB）",
            "Command output kept (KB)");
        Loc.Register(
            BashCaptureHintKey,
            "一次命令保留的输出量，超出部分丢弃并在结果里标注。默认 512。调小省上下文，调大排查更完整；一次构建日志可能吃掉整轮预算。",
            "How much of one command's output is kept; the rest is dropped and the result says so. Default 512. Lower saves context, higher keeps more diagnostics — one build log can eat a turn's budget.");
        Loc.Register(
            GrepResultsKey,
            "grep 结果上限（行）",
            "Search results (lines)");
        Loc.Register(
            GrepResultsHintKey,
            "一次搜索返回的匹配行数上限，超出部分被截断并报出命中总数。默认 1000。",
            "Matching lines one search returns; past it the result is cut and reports how many matched in all. Default 1000.");
        Loc.Register(
            IgnoredDirectoriesKey,
            "搜索跳过的目录",
            "Directories a search skips");
        Loc.Register(
            IgnoredDirectoriesHintKey,
            "按目录名跳过构建产物，逗号分隔。只作用于搜索起点之下的目录：直接搜 obj 仍然搜它。默认 bin, obj, .vs, node_modules, target, .gradle, .venv, venv, __pycache__, .pytest_cache, .mypy_cache, .ruff_cache, .tox, .turbo, .next, .nuxt, .dart_tool, .zig-cache, .idea。",
            "Build output skipped by directory name, comma separated. It applies below the path a search was given: searching obj itself still searches it. Defaults to bin, obj, .vs, node_modules, target, .gradle, .venv, venv, __pycache__, .pytest_cache, .mypy_cache, .ruff_cache, .tox, .turbo, .next, .nuxt, .dart_tool, .zig-cache, .idea.");
        Loc.Register(
            SpillKey,
            "artifact 溢出阈值（字符）",
            "Artifact spill threshold (characters)");
        Loc.Register(
            SpillHintKey,
            "工具结果超过此值就存成 artifact，上下文只留首尾预览 + id，模型可用 read_artifact 分次取回。默认 32000。改动在下一个会话宿主生效。",
            "A tool result past this size is stored as an artifact: the context keeps a head+tail preview and the id, and the model reads the rest with read_artifact. Default 32000. A change applies to the next session host.");
        Loc.Register(
            PreviewKey,
            "artifact 预览（字符）",
            "Artifact preview (characters)");
        Loc.Register(
            PreviewHintKey,
            "溢出后留在上下文里的字符数（头尾各半），最多为阈值的一半。默认 2000。太小模型会反复重读，太大等于没溢出。",
            "Characters of a spilled result that stay inline (half from the head, half from the tail), capped at half the threshold. Default 2000. Too small and the model re-reads it, too large and nothing was saved.");
    }
}
