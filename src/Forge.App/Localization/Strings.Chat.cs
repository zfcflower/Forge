using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The chat feature's own string table (forge-app spec, "偏好、文案与设置分区"): the <c>chat</c> settings section —
/// tool-result thumbnails in the transcript and whether those images reach the model. Keys are namespaced
/// <c>chat.*</c> and every text is registered from a module initializer, so no shared table (and no parallel feature)
/// has to be edited.
/// </summary>
internal static class ChatStrings
{
    /// <summary>Title of the settings section that holds the tool-image rows.</summary>
    public const string SettingsTitle = "chat.settings.title";

    public const string SettingsThumbnailEdge = "chat.settings.thumbnailEdge";
    public const string SettingsSendToolImages = "chat.settings.sendToolImages";

    /// <summary>Wave W11: which Mermaid diagram types the transcript and the diff preview draw.</summary>
    public const string SettingsMermaidTypes = "chat.settings.mermaidTypes";
    public const string SettingsMermaidTypesHint = "chat.settings.mermaidTypesHint";

    /// <summary>Wave W11: how long one Mermaid diagram may take to render.</summary>
    public const string SettingsMermaidTimeout = "chat.settings.mermaidTimeout";

    /// <summary>The caption under both rows: the thumbnail never re-encodes the stored image and the switch only changes what the model receives.</summary>
    public const string SettingsHint = "chat.settings.hint";

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(SettingsTitle, "对话", "Chat");
        Loc.Register(SettingsThumbnailEdge, "工具图片缩略图最长边（像素）", "Tool image thumbnail edge (pixels)");
        Loc.Register(SettingsSendToolImages, "把工具图片发送给模型", "Send tool images to the model");
        Loc.Register(SettingsMermaidTypes, "Mermaid 图类型", "Mermaid diagram types");
        Loc.Register(
            SettingsMermaidTypesHint,
            "逗号分隔；未列出的类型按普通代码块显示，改下一次渲染生效。",
            "Comma separated; types not listed stay ordinary code blocks, and a change applies to the next render.");
        Loc.Register(SettingsMermaidTimeout, "Mermaid 渲染超时（毫秒）", "Mermaid render timeout (ms)");
        Loc.Register(
            SettingsHint,
            "缩略图只约束会话记录里显示时解码的大小，从不重新编码已存储的图片；此开关只决定模型是否收到这些图片，记录始终照常显示。",
            "The thumbnail edge only bounds how large a tool image is decoded for the transcript; the stored image is never re-encoded. The switch only decides whether the model receives the images — the transcript always shows them.");
    }
}
