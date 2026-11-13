using System.Runtime.CompilerServices;
using Forge.App.ViewModels.Preview;

namespace Forge.App.Localization;

/// <summary>
/// Texts of the file panel's read-only previews (image, CSV/TSV table, notebook, the placeholder cards) and of their
/// settings section. Keys: <c>files.preview.*</c>. The table mirrors the other feature tables: parameterized texts are
/// typed accessors resolved when they are read, count-dependent nouns come in <c>.one</c>/<c>.other</c> pairs.
/// </summary>
internal static class PreviewStrings
{
    public const string SectionTitleKey = "files.preview.settings.sectionTitle";
    public const string SettingsRowsKey = "files.preview.settings.rows";
    public const string SettingsNotebookCharsKey = "files.preview.settings.notebookChars";
    public const string SettingsImageLimitKey = "files.preview.settings.imageLimit";
    public const string SettingsHintKey = "files.preview.settings.hint";

    public const string OpenExternalKey = "files.preview.openExternal";
    public const string OpenFailedKey = "files.preview.openFailed";
    public const string ShowSourceKey = "files.preview.showSource";
    public const string SizeKey = "files.preview.size";
    public const string TruncatedKey = "files.preview.truncated";

    public const string LfsCaptionKey = "files.preview.lfs.caption";
    public const string LfsReasonKey = "files.preview.lfs.reason";
    public const string LfsOidKey = "files.preview.lfs.oid";
    public const string LfsSizeKey = "files.preview.lfs.size";
    public const string LfsVersionKey = "files.preview.lfs.version";

    public const string BinaryCaptionKey = "files.preview.binary.caption";
    public const string BinaryReasonKey = "files.preview.binary.reason";

    public const string PdfCaptionKey = "files.preview.pdf.caption";
    public const string PdfReasonKey = "files.preview.pdf.reason";

    public const string HtmlCaptionKey = "files.preview.html.caption";
    public const string HtmlReasonKey = "files.preview.html.reason";

    public const string ImageCaptionKey = "files.preview.image.caption";
    public const string ImageDecodeFailedKey = "files.preview.image.decodeFailed";
    public const string ImageReadFailedKey = "files.preview.image.readFailed";
    public const string ImageTooLargeCaptionKey = "files.preview.image.tooLarge.caption";
    public const string ImageTooLargeReasonKey = "files.preview.image.tooLarge.reason";

    public const string TableCaptionKey = "files.preview.table.caption";
    public const string TableRowsBase = "files.preview.table.rows";
    public const string TableRowsSkippedKey = "files.preview.table.rowsSkipped";
    public const string TableReadFailedKey = "files.preview.table.readFailed";

    public const string NotebookCaptionBase = "files.preview.notebook.caption";
    public const string NotebookParseFailedKey = "files.preview.notebook.parseFailed";
    public const string NotebookReadFailedKey = "files.preview.notebook.readFailed";

    public const string NotebookTooLargeKey = "files.preview.notebook.tooLarge";
    public const string NotebookCellMarkdownKey = "files.preview.notebook.cell.markdown";
    public const string NotebookCellCodeKey = "files.preview.notebook.cell.code";
    public const string NotebookCellRawKey = "files.preview.notebook.cell.raw";
    public const string NotebookOutputStreamKey = "files.preview.notebook.output.stream";
    public const string NotebookOutputResultKey = "files.preview.notebook.output.result";
    public const string NotebookOutputErrorKey = "files.preview.notebook.output.error";

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(SectionTitleKey, "文件预览", "File preview");
        Loc.Register(SettingsRowsKey, "CSV/TSV 预览行数", "CSV/TSV preview rows");
        Loc.Register(SettingsNotebookCharsKey, "Notebook 输出字符数", "Notebook output characters");
        Loc.Register(SettingsImageLimitKey, "图片预览上限（MB）", "Image preview limit (MB)");
        Loc.Register(SettingsHintKey, "文件预览的读取上限：表格预览解析的行数、Notebook 单个输出的字符数、图片预览允许的最大文件大小。超过图片上限的文件显示占位卡片而不解码。",
            "Limits for the file viewer: rows parsed for a CSV/TSV table, characters kept per notebook output, and the largest file the image preview decodes. Files above the image limit show a placeholder card instead of being decoded.");
        Loc.Register(OpenExternalKey, "用系统程序打开", "Open with the system app");
        Loc.Register(OpenFailedKey, "无法用系统程序打开 {0}：{1}", "Could not open {0} with the system app: {1}");
        Loc.Register(ShowSourceKey, "查看源码", "Show source");
        Loc.Register(SizeKey, "{0} 字节", "{0} bytes");
        Loc.Register(TruncatedKey, "…（已截断）", "... (truncated)");
        Loc.Register(LfsCaptionKey, "Git LFS 指针", "Git LFS pointer");
        Loc.Register(LfsReasonKey, "这是 Git LFS 指针文件：真实内容存放在 LFS 存储中。", "This is a Git LFS pointer file: the real bytes live in the LFS store.");
        Loc.Register(LfsOidKey, "OID", "OID");
        Loc.Register(LfsSizeKey, "大小", "Size");
        Loc.Register(LfsVersionKey, "版本", "Version");
        Loc.Register(BinaryCaptionKey, "二进制文件", "Binary file");
        Loc.Register(BinaryReasonKey, "二进制内容无法作为文本显示。", "Binary content cannot be shown as text.");
        Loc.Register(PdfCaptionKey, "PDF 文档", "PDF document");
        Loc.Register(PdfReasonKey, "PDF 文档在此处不渲染；请用系统查看器打开。", "PDF documents are not rendered here; open this one with the system viewer.");
        Loc.Register(HtmlCaptionKey, "HTML 文档", "HTML document");
        Loc.Register(HtmlReasonKey, "网页在此处不渲染；请用系统浏览器打开，或查看源码。", "Web pages are not rendered here; open in the system browser, or show the source.");
        Loc.Register(ImageCaptionKey, "{0} 图片 · {1}×{2} · {3}", "{0} image · {1}×{2} · {3}");
        Loc.Register(ImageDecodeFailedKey, "无法解码 {0}。", "Could not decode {0}.");
        Loc.Register(ImageReadFailedKey, "无法读取 {0}：{1}", "Could not read {0}: {1}");
        Loc.Register(ImageTooLargeCaptionKey, "图片过大", "Image too large");
        Loc.Register(ImageTooLargeReasonKey, "图片超过预览上限（{0} MB），未解码；请用系统查看器打开。", "The image exceeds the {0} MB preview limit and was not decoded; open with the system viewer.");
        Loc.Register(TableCaptionKey, "{0} · {1} · {2}", "{0} · {1} · {2}");
        Loc.Register(TableRowsBase + ".one", "{0} 行", "{0} row");
        Loc.Register(TableRowsBase + ".other", "{0} 行", "{0} rows");
        Loc.Register(TableRowsSkippedKey, "显示 {0}，跳过 {1}", "Showing {0}, {1} skipped");
        Loc.Register(TableReadFailedKey, "无法读取 {0}：{1}", "Could not read {0}: {1}");
        Loc.Register(NotebookCaptionBase + ".one", "{0} · {1} 个单元格", "{0} · {1} cell");
        Loc.Register(NotebookCaptionBase + ".other", "{0} · {1} 个单元格", "{0} · {1} cells");
        Loc.Register(NotebookParseFailedKey, "{0} 不是有效的笔记本（JSON）文件：{1}", "{0} is not a valid notebook (JSON) file: {1}");
        Loc.Register(NotebookReadFailedKey, "无法读取 {0}：{1}", "Could not read {0}: {1}");
        Loc.Register(NotebookTooLargeKey, "{0} 超过 {1} MB，笔记本预览不打开这么大的文件。", "{0} is over {1} MB; the notebook preview does not open files that large.");
        Loc.Register(NotebookCellMarkdownKey, "Markdown", "Markdown");
        Loc.Register(NotebookCellCodeKey, "代码", "Code");
        Loc.Register(NotebookCellRawKey, "原始", "Raw");
        Loc.Register(NotebookOutputStreamKey, "输出", "Output");
        Loc.Register(NotebookOutputResultKey, "结果", "Result");
        Loc.Register(NotebookOutputErrorKey, "错误", "Error");
    }

    public static string SectionTitle => Loc.Instance[SectionTitleKey];

    public static string SettingsRowsLabel => Loc.Instance[SettingsRowsKey];

    public static string SettingsNotebookCharsLabel => Loc.Instance[SettingsNotebookCharsKey];

    public static string SettingsImageLimitLabel => Loc.Instance[SettingsImageLimitKey];

    public static string SettingsHint => Loc.Instance[SettingsHintKey];

    /// <summary>The byte size shown on placeholder cards, grouped with its unit.</summary>
    public static string ByteSize(string bytes) => Loc.Instance.Format(SizeKey, bytes);

    /// <summary>The marker appended to a truncated notebook output, resolved at truncation time.</summary>
    public static string TruncatedMarker => Loc.Instance[TruncatedKey];

    public static LocText OpenFailed(string name, string message) =>
        new(OpenFailedKey, LocText.Literal(name), LocText.Literal(message));

    public static LocText ImageCaptionText(string name, int width, int height, string size) =>
        new(ImageCaptionKey, LocText.Literal(name), TextFormat.Id(width), TextFormat.Id(height), LocText.Literal(size));

    public static LocText ImageDecodeFailed(string name) => new(ImageDecodeFailedKey, LocText.Literal(name));

    public static LocText ImageReadFailed(string name, string message) =>
        new(ImageReadFailedKey, LocText.Literal(name), LocText.Literal(message));

    public static LocText ImageTooLargeReason(int limitMiB) => new(ImageTooLargeReasonKey, TextFormat.Number(limitMiB));

    public static string TableCaption(string name, string format, LocText rows) =>
        Loc.Instance.Format(TableCaptionKey, name, format, rows);

    /// <summary>"1,000 行" / "1,000 rows", or "显示 50 行，跳过 950 行" when the row limit cut the file short.</summary>
    public static LocText TableSummary(int shown, int skipped) =>
        skipped == 0
            ? LocText.Plural(TableRowsBase, shown, TextFormat.Number(shown))
            : new LocText(TableRowsSkippedKey, LocText.Plural(TableRowsBase, shown, TextFormat.Number(shown)), TextFormat.Number(skipped));

    public static LocText TableReadFailed(string name, string message) =>
        new(TableReadFailedKey, LocText.Literal(name), LocText.Literal(message));

    public static LocText NotebookCaption(string name, int cells) =>
        LocText.Plural(NotebookCaptionBase, cells, LocText.Literal(name), TextFormat.Number(cells));

    public static LocText NotebookParseFailed(string name, string message) =>
        new(NotebookParseFailedKey, LocText.Literal(name), LocText.Literal(message));

    public static LocText NotebookReadFailed(string name, string message) =>
        new(NotebookReadFailedKey, LocText.Literal(name), LocText.Literal(message));

    /// <summary>"<file> is over <n> MB; the notebook preview does not open files that large."</summary>
    public static LocText NotebookTooLarge(string name, long limitMegabytes) => new(
        NotebookTooLargeKey,
        LocText.Literal(name),
        LocText.Literal(TextFormat.Number(limitMegabytes)));

    public static string NotebookCellKindLabel(NotebookCellKind kind) => Loc.Instance[CellKindKey(kind)];

    public static string NotebookOutputKindLabel(NotebookOutputKind kind) => Loc.Instance[OutputKindKey(kind)];

    private static string CellKindKey(NotebookCellKind kind) => kind switch
    {
        NotebookCellKind.Markdown => NotebookCellMarkdownKey,
        NotebookCellKind.Code => NotebookCellCodeKey,
        _ => NotebookCellRawKey,
    };

    private static string OutputKindKey(NotebookOutputKind kind) => kind switch
    {
        NotebookOutputKind.Stream => NotebookOutputStreamKey,
        NotebookOutputKind.Error => NotebookOutputErrorKey,
        _ => NotebookOutputResultKey,
    };
}
