using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>Texts of image attachments: the composer's attach button, thumbnails and rejection messages.</summary>
internal static class ImageStrings
{
    public const string AttachKey = "session.images.attach";
    public const string AttachTooltipKey = "session.images.attach.tooltip";
    public const string RemoveKey = "session.images.remove";
    public const string ModelRejectsKey = "session.images.modelRejects";
    public const string TooManyKey = "session.images.tooMany";
    public const string TooLargeKey = "session.images.tooLarge";
    public const string UnsupportedKey = "session.images.unsupported";
    public const string ReadFailedKey = "session.images.readFailed";
    public const string PickerTitleKey = "session.images.pickerTitle";
    public const string FileTypeKey = "session.images.fileType";
    public const string CountKey = "session.images.count";
    public const string CaptionKey = "session.images.caption";
    public const string ClipboardNameKey = "session.images.clipboardName";
    public const string DismissKey = "session.images.dismiss";

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(AttachKey, "添加图片", "Attach images");
        Loc.Register(AttachTooltipKey, "添加图片（也可粘贴或拖入）", "Attach images (or paste or drop them here)");
        Loc.Register(RemoveKey, "移除图片", "Remove image");
        Loc.Register(ModelRejectsKey, "当前模型不接受图片输入", "The current model does not accept image input");
        Loc.Register(TooManyKey, "每条消息最多 {0} 张图片", "At most {0} images per message");
        Loc.Register(TooLargeKey, "{0} 缩放后仍超过 {1} MB，未添加", "{0} is still over {1} MB after resizing and was not added");
        Loc.Register(UnsupportedKey, "{0} 不是可读取的 PNG、JPEG、GIF 或 WebP 图片", "{0} is not a readable PNG, JPEG, GIF or WebP image");
        Loc.Register(ReadFailedKey, "无法读取 {0}：{1}", "Could not read {0}: {1}");
        Loc.Register(PickerTitleKey, "选择图片", "Choose images");
        Loc.Register(FileTypeKey, "图片", "Images");
        Loc.Register(CountKey, "图片 ×{0}", "Images ×{0}");
        Loc.Register(CaptionKey, "{0} · {1} KB", "{0} · {1} KB");
        Loc.Register(ClipboardNameKey, "剪贴板图片", "Clipboard image");
        Loc.Register(DismissKey, "关闭提示", "Dismiss");
    }

    public static string ModelRejects => Loc.Instance[ModelRejectsKey];

    public static string TooMany => Loc.Instance.Format(TooManyKey, ImagesMax());

    public static string TooLarge(string name) =>
        Loc.Instance.Format(TooLargeKey, name, (Forge.App.ViewModels.ImageAttachmentRules.MaxEncodedBytes / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static string Unsupported(string name) => Loc.Instance.Format(UnsupportedKey, name);

    public static string ReadFailed(string name, string reason) => Loc.Instance.Format(ReadFailedKey, name, reason);

    public static string PickerTitle => Loc.Instance[PickerTitleKey];

    public static string FileType => Loc.Instance[FileTypeKey];

    public static string ClipboardName => Loc.Instance[ClipboardNameKey];

    public static string Count(int count) => Loc.Instance.Format(CountKey, TextFormat.Number(count));

    /// <summary>"PNG · 123 KB": the format's short name and the encoded size.</summary>
    public static string Caption(string mediaType, long kilobytes)
    {
        var slash = mediaType.IndexOf('/');
        var name = (slash < 0 ? mediaType : mediaType[(slash + 1)..]).ToUpperInvariant();
        return Loc.Instance.Format(CaptionKey, name, TextFormat.Number(kilobytes));
    }

    private static string ImagesMax() => TextFormat.Number(Forge.App.ViewModels.ImageAttachmentRules.MaxImagesPerMessage);
}
