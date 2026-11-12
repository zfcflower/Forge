using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.App.ViewModels;

namespace Forge.App.Localization;

/// <summary>
/// Texts of non-image attachments (PDF, audio, video, text and source files) and of the shared attachment gate: the attach
/// button, the file chips, the refusal messages and toasts, and the hint while the current model cannot take what is
/// attached. Image-only texts stay in <see cref="ImageStrings"/>.
/// </summary>
internal static class AttachmentStrings
{
    public const string AttachKey = "session.attachments.attach";
    public const string AttachTooltipKey = "session.attachments.attach.tooltip";
    public const string RemoveKey = "session.attachments.remove";
    public const string PickerTitleKey = "session.attachments.pickerTitle";
    public const string AllTypesKey = "session.attachments.fileType.all";
    public const string PdfTypeKey = "session.attachments.fileType.pdf";
    public const string AudioTypeKey = "session.attachments.fileType.audio";
    public const string VideoTypeKey = "session.attachments.fileType.video";
    public const string TextTypeKey = "session.attachments.fileType.text";
    public const string KindImageKey = "session.attachments.kind.image";
    public const string KindPdfKey = "session.attachments.kind.pdf";
    public const string KindAudioKey = "session.attachments.kind.audio";
    public const string KindVideoKey = "session.attachments.kind.video";
    public const string KindTextKey = "session.attachments.kind.text";
    public const string ModelRejectsKey = "session.attachments.modelRejects";
    public const string WireUnsupportedKey = "session.attachments.wireUnsupported";
    public const string TooLargeKey = "session.attachments.tooLarge";
    public const string TooManyKey = "session.attachments.tooMany";
    public const string NotTextKey = "session.attachments.notText";
    public const string UnsupportedKey = "session.attachments.unsupported";
    public const string RefusedTitleKey = "session.attachments.refusedTitle";
    public const string BlockedTitleKey = "session.attachments.blockedTitle";
    public const string BlockedKey = "session.attachments.blocked";
    public const string CountKey = "session.attachments.count";
    public const string CaptionKey = "session.attachments.caption";

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(AttachKey, "添加附件", "Attach files");
        Loc.Register(
            AttachTooltipKey,
            "添加图片、PDF、音频、视频或文本文件（也可粘贴或拖入）",
            "Attach images, PDFs, audio, video or text files (or paste or drop them here)");
        Loc.Register(RemoveKey, "移除附件", "Remove attachment");
        Loc.Register(PickerTitleKey, "选择附件", "Choose files to attach");
        Loc.Register(AllTypesKey, "所有支持的文件", "All supported files");
        Loc.Register(PdfTypeKey, "PDF 文档", "PDF documents");
        Loc.Register(AudioTypeKey, "音频", "Audio");
        Loc.Register(VideoTypeKey, "视频", "Video");
        Loc.Register(TextTypeKey, "文本与源码", "Text and source files");
        Loc.Register(KindImageKey, "图片", "images");
        Loc.Register(KindPdfKey, "PDF", "PDF");
        Loc.Register(KindAudioKey, "音频", "audio");
        Loc.Register(KindVideoKey, "视频", "video");
        Loc.Register(KindTextKey, "文本", "text");
        Loc.Register(ModelRejectsKey, "{0}：当前模型不接受{1}输入，未添加", "{0}: the current model does not accept {1} input; not added");
        Loc.Register(
            WireUnsupportedKey,
            "{0}：当前提供商的接口无法发送这种{1}（{2}），未添加",
            "{0}: the current provider's API cannot send this {1} ({2}); not added");
        Loc.Register(TooLargeKey, "{0} 超过 {1} 的上限，未添加", "{0} is over the {1} limit and was not added");
        Loc.Register(TooManyKey, "每条消息最多 {0} 个文件附件", "At most {0} file attachments per message");
        Loc.Register(NotTextKey, "{0} 不是 UTF-8 文本，未添加", "{0} is not UTF-8 text and was not added");
        Loc.Register(
            UnsupportedKey,
            "{0} 不是支持的附件（图片、PDF、音频、视频或文本文件）",
            "{0} is not a supported attachment (image, PDF, audio, video or text file)");
        Loc.Register(RefusedTitleKey, "附件未添加", "Attachment not added");
        Loc.Register(BlockedTitleKey, "当前模型不接受部分附件", "The current model cannot take some attachments");
        Loc.Register(
            BlockedKey,
            "当前模型或提供商不接受已添加的{0}，移除后才能发送",
            "The current model or provider does not accept the attached {0}; remove them to send");
        Loc.Register(CountKey, "附件 ×{0}", "Attachments ×{0}");
        Loc.Register(CaptionKey, "{0} · {1}", "{0} · {1}");
    }

    public static LocText RefusedTitle => new(RefusedTitleKey);

    public static LocText BlockedTitle => new(BlockedTitleKey);

    public static string PickerTitle => Loc.Instance[PickerTitleKey];

    public static string AllTypes => Loc.Instance[AllTypesKey];

    public static string PdfType => Loc.Instance[PdfTypeKey];

    public static string AudioType => Loc.Instance[AudioTypeKey];

    public static string VideoType => Loc.Instance[VideoTypeKey];

    public static string TextType => Loc.Instance[TextTypeKey];

    public static LocText ModelRejects(string name, AttachmentKind kind) => new(ModelRejectsKey, name, KindName(kind));

    public static LocText WireUnsupported(string name, AttachmentKind kind, string mediaType) =>
        new(WireUnsupportedKey, name, KindName(kind), mediaType);

    public static LocText TooLarge(string name, long limitBytes) => new(TooLargeKey, name, Size(limitBytes));

    public static LocText NotText(string name) => new(NotTextKey, name);

    public static LocText Unsupported(string name) => new(UnsupportedKey, name);

    public static string TooMany => Loc.Instance.Format(TooManyKey, TextFormat.Number(FileAttachmentRules.MaxFilesPerMessage));

    public static string Count(int count) => Loc.Instance.Format(CountKey, TextFormat.Number(count));

    /// <summary>The hint while attachments block sending: the kinds the current model or provider refuses.</summary>
    public static LocText Blocked(ModelInputs kinds) => new(BlockedKey, KindList(kinds));

    /// <summary>"PDF · 12 KB": the kind's short label and the size.</summary>
    public static string Caption(string label, long bytes) => Loc.Instance.Format(CaptionKey, label, Size(bytes));

    /// <summary>A size for captions and limits: whole KB under one MB, whole MB from there (rounded up, never zero).</summary>
    public static string Size(long bytes)
    {
        if (bytes >= 1024 * 1024)
        {
            var megabytes = (bytes + 1024 * 1024 - 1) / (1024 * 1024);
            return TextFormat.Number(megabytes) + " MB";
        }

        return TextFormat.Number(Math.Max(1, (bytes + 1023) / 1024)) + " KB";
    }

    /// <summary>The localized name of a kind, resolved when the enclosing text is resolved.</summary>
    public static LocText KindName(AttachmentKind kind) => new(kind switch
    {
        AttachmentKind.Image => KindImageKey,
        AttachmentKind.Pdf => KindPdfKey,
        AttachmentKind.Audio => KindAudioKey,
        AttachmentKind.Video => KindVideoKey,
        _ => KindTextKey,
    });

    private static LocText KindList(ModelInputs kinds)
    {
        var parts = new List<object?>(4);
        Add(ModelInputs.Image, AttachmentKind.Image);
        Add(ModelInputs.Pdf, AttachmentKind.Pdf);
        Add(ModelInputs.Audio, AttachmentKind.Audio);
        Add(ModelInputs.Video, AttachmentKind.Video);
        return parts.Count == 0 ? KindName(AttachmentKind.Text) : new LocText(JoinKey(parts.Count), [.. parts]);

        void Add(ModelInputs flag, AttachmentKind kind)
        {
            if ((kinds & flag) != 0)
            {
                parts.Add(KindName(kind));
            }
        }
    }

    // One registered pattern per list length keeps the separator localized without building text by hand.
    private const string Join1Key = "session.attachments.kind.join1";
    private const string Join2Key = "session.attachments.kind.join2";
    private const string Join3Key = "session.attachments.kind.join3";
    private const string Join4Key = "session.attachments.kind.join4";

    [ModuleInitializer]
    internal static void RegisterJoins()
    {
        Loc.Register(Join1Key, "{0}", "{0}");
        Loc.Register(Join2Key, "{0}、{1}", "{0} and {1}");
        Loc.Register(Join3Key, "{0}、{1}、{2}", "{0}, {1} and {2}");
        Loc.Register(Join4Key, "{0}、{1}、{2}、{3}", "{0}, {1}, {2} and {3}");
    }

    private static string JoinKey(int count) => count switch
    {
        1 => Join1Key,
        2 => Join2Key,
        3 => Join3Key,
        _ => Join4Key,
    };
}
