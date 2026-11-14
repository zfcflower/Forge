using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The Mermaid feature's own string table (wave W11): the block's states, its source/diagram toggle and the prompt that hands
/// a broken diagram back to the model. Keys are namespaced <c>mermaid.*</c> and registered from a module initializer, so no
/// shared table has to be edited. The two settings rows live in <see cref="ChatStrings"/> with the rest of the chat section.
/// </summary>
internal static class MermaidStrings
{
    /// <summary>Caption while the render runs in the background.</summary>
    public const string Rendering = "mermaid.rendering";

    /// <summary>Heading of the failure panel; the renderer's own message follows it.</summary>
    public const string RenderFailed = "mermaid.renderFailed";

    /// <summary>Failure text for <see cref="Services.MermaidRenderFailure.Timeout"/>; <c>{0}</c> is the configured timeout.</summary>
    public const string Timeout = "mermaid.timeout";

    /// <summary>Failure text for <see cref="Services.MermaidRenderFailure.TooLarge"/>.</summary>
    public const string TooLarge = "mermaid.tooLarge";

    /// <summary>Failure text for <see cref="Services.MermaidRenderFailure.Internal"/>.</summary>
    public const string Internal = "mermaid.internal";

    /// <summary>Where an error points, when the message named a line; <c>{0}</c> is the line number.</summary>
    public const string Line = "mermaid.line";

    public const string ShowSource = "mermaid.showSource";
    public const string ShowDiagram = "mermaid.showDiagram";
    public const string Retry = "mermaid.retry";

    /// <summary>Hands the broken diagram back to the model.</summary>
    public const string Fix = "mermaid.fix";
    public const string FixSent = "mermaid.fixSent";
    public const string FixQueued = "mermaid.fixQueued";

    /// <summary>Shown while the hand-off is in flight (the turn may take a while); replaced by sent/queued when it returns.</summary>
    public const string FixSending = "mermaid.fixSending";

    /// <summary>The message sent to the model: <c>{0}</c> error, <c>{1}</c> source, <c>{2}</c> the fence to wrap it in.</summary>
    public const string FixRequest = "mermaid.fixRequest";

    /// <summary>Appended when the source had to be cut to fit the message.</summary>
    public const string FixTruncated = "mermaid.fixTruncated";

    /// <summary>Origin shown on the turn this hand-off starts.</summary>
    public const string FixOrigin = "mermaid.origin";

    /// <summary>Accessible name of a rendered diagram.</summary>
    public const string Diagram = "mermaid.diagram";

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(Rendering, "正在渲染图…", "Rendering the diagram…");
        Loc.Register(RenderFailed, "Mermaid 图渲染失败", "The Mermaid diagram could not be rendered");
        Loc.Register(Timeout, "渲染超过 {0} 毫秒，已放弃", "Rendering took longer than {0} ms and was abandoned");
        Loc.Register(TooLarge, "图太大，未渲染", "The diagram is too large to render");
        Loc.Register(Internal, "渲染器无法处理这张图", "The renderer could not draw this diagram");
        Loc.Register(Line, "第 {0} 行", "line {0}");
        Loc.Register(ShowSource, "显示源码", "Show source");
        Loc.Register(ShowDiagram, "显示图", "Show diagram");
        Loc.Register(Retry, "重试", "Retry");
        Loc.Register(Fix, "让模型修正", "Ask the model to fix");
        Loc.Register(FixSent, "已发送给模型", "Sent to the model");
        Loc.Register(FixSending, "已交给模型，等待回复…", "Handed to the model, waiting for its reply…");
        Loc.Register(FixQueued, "已放入输入框，发送后交给模型", "Added to the prompt box; send it to hand it over");
        Loc.Register(
            FixRequest,
            "这张 Mermaid 图在本地渲染失败，请修正它。\n\n错误：{0}\n\n源码：\n```{2}mermaid\n{1}\n```{2}\n\n请只输出修正后的完整 Mermaid 代码块。",
            "This Mermaid diagram failed to render locally; please fix it.\n\nError: {0}\n\nSource:\n```{2}mermaid\n{1}\n```{2}\n\nReply with the corrected, complete Mermaid code block only.");
        Loc.Register(FixTruncated, "…（源码过长，已截断）", "… (the source was truncated)");
        Loc.Register(FixOrigin, "Mermaid 图修正", "Mermaid diagram fix");
        Loc.Register(Diagram, "Mermaid 图", "Mermaid diagram");
    }
}
