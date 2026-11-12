using System.Runtime.CompilerServices;
using Forge.Abstractions;

namespace Forge.App.Localization;

/// <summary>
/// String table of the <c>prompts</c> settings section (prompt templates: commands, agents and built-in prompt slots).
/// Keys are namespaced with <c>prompts.</c> and registered from <see cref="Initialize"/>. Built-in names and descriptions
/// come from Core in English and are shown as-is; template texts are model input and never localized.
/// </summary>
internal static class PromptsStrings
{
    public const string SectionTitleKey = "prompts.section.title";
    public const string HintKey = "prompts.hint";

    public const string FilterAllKey = "prompts.filter.all";
    public const string FilterBuiltInKey = "prompts.filter.builtIn";
    public const string FilterCustomKey = "prompts.filter.custom";

    public const string GroupCommandsKey = "prompts.group.commands";
    public const string GroupAgentsKey = "prompts.group.agents";
    public const string GroupSlotsKey = "prompts.group.slots";

    public const string KindCommandKey = "prompts.kind.command";
    public const string KindAgentKey = "prompts.kind.agent";
    public const string KindSlotKey = "prompts.kind.slot";

    public const string ScopeBuiltInKey = "prompts.scope.builtIn";
    public const string ScopeGlobalKey = "prompts.scope.global";
    public const string ScopeProjectKey = "prompts.scope.project";

    public const string BadgeOverridesKey = "prompts.badge.overrides";
    public const string BadgeAppendKey = "prompts.badge.append";
    public const string BadgeShadowedKey = "prompts.badge.shadowed";
    public const string BadgeInvalidKey = "prompts.badge.invalid";

    public const string EmptyKey = "prompts.list.empty";
    public const string SelectHintKey = "prompts.editor.selectHint";
    public const string NewKey = "prompts.editor.new";
    public const string IdLabelKey = "prompts.editor.id";
    public const string KindLabelKey = "prompts.editor.kind";
    public const string ScopeLabelKey = "prompts.editor.scope";
    public const string FileLabelKey = "prompts.editor.file";
    public const string TextLabelKey = "prompts.editor.text";
    public const string BuiltInReadOnlyKey = "prompts.editor.builtInReadOnly";
    public const string AppendHintKey = "prompts.editor.appendHint";
    public const string ProblemsTitleKey = "prompts.editor.problems";
    public const string NoWorkspaceKey = "prompts.editor.noWorkspace";

    public const string CopyGlobalKey = "prompts.action.copyGlobal";
    public const string CopyProjectKey = "prompts.action.copyProject";
    public const string SaveKey = "prompts.action.save";
    public const string CreateKey = "prompts.action.create";
    public const string RevertKey = "prompts.action.revert";
    public const string DeleteKey = "prompts.action.delete";
    public const string DeleteConfirmKey = "prompts.action.deleteConfirm";
    public const string DeleteCancelKey = "prompts.action.deleteCancel";

    public const string MsgSavedKey = "prompts.message.saved";
    public const string MsgDeletedKey = "prompts.message.deleted";
    public const string MsgCopiedKey = "prompts.message.copied";
    public const string MsgConflictKey = "prompts.message.conflict";
    public const string MsgExistsKey = "prompts.message.exists";
    public const string MsgSlotNewKey = "prompts.message.slotNew";
    public const string MsgInvalidIdKey = "prompts.message.invalidId";
    public const string MsgInvalidKey = "prompts.message.invalid";
    public const string MsgReadOnlyKey = "prompts.message.readOnly";
    public const string MsgGoneKey = "prompts.message.gone";
    public const string MsgFailedKey = "prompts.message.failed";
    public const string MsgUnsavedKey = "prompts.message.unsaved";

    public const string ProblemInvalidIdKey = "prompts.problem.invalidId";
    public const string ProblemUnclosedFrontmatterKey = "prompts.problem.unclosedFrontmatter";
    public const string ProblemUnknownKeyKey = "prompts.problem.unknownKey";
    public const string ProblemInvalidValueKey = "prompts.problem.invalidValue";
    public const string ProblemEmptyBodyKey = "prompts.problem.emptyBody";
    public const string ProblemUnknownSlotKey = "prompts.problem.unknownSlot";
    public const string ProblemUnknownPlaceholderKey = "prompts.problem.unknownPlaceholder";
    public const string ProblemTooLargeKey = "prompts.problem.tooLarge";
    public const string ProblemUnreadableKey = "prompts.problem.unreadable";

    /// <summary>The localized text of one validation problem; the detail (key, name, message) goes into <c>{0}</c> where the text has one.</summary>
    public static string Problem(PromptTemplateProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var key = problem.Code switch
        {
            PromptTemplateProblemCode.InvalidId => ProblemInvalidIdKey,
            PromptTemplateProblemCode.UnclosedFrontmatter => ProblemUnclosedFrontmatterKey,
            PromptTemplateProblemCode.UnknownKey => ProblemUnknownKeyKey,
            PromptTemplateProblemCode.InvalidValue => ProblemInvalidValueKey,
            PromptTemplateProblemCode.EmptyBody => ProblemEmptyBodyKey,
            PromptTemplateProblemCode.UnknownSlot => ProblemUnknownSlotKey,
            PromptTemplateProblemCode.UnknownPlaceholder => ProblemUnknownPlaceholderKey,
            PromptTemplateProblemCode.TooLarge => ProblemTooLargeKey,
            _ => ProblemUnreadableKey,
        };
        return Loc.Instance.Format(key, problem.Detail ?? string.Empty);
    }

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    internal static void Register()
    {
        Loc.Register(SectionTitleKey, "提示词", "Prompts");
        Loc.Register(HintKey, "命令、Agent 与内置提示词。内置项只读;复制到全局或项目后即可修改,删除自己的那份就恢复默认。", "Commands, agents and built-in prompts. Built-ins are read-only; copy one to global or project to change it, and delete your copy to restore the default.");

        Loc.Register(FilterAllKey, "全部", "All");
        Loc.Register(FilterBuiltInKey, "内置", "Built-in");
        Loc.Register(FilterCustomKey, "自定义", "Custom");

        Loc.Register(GroupCommandsKey, "命令", "Commands");
        Loc.Register(GroupAgentsKey, "Agent", "Agents");
        Loc.Register(GroupSlotsKey, "内置提示词", "Built-in prompts");

        Loc.Register(KindCommandKey, "命令", "Command");
        Loc.Register(KindAgentKey, "Agent", "Agent");
        Loc.Register(KindSlotKey, "内置提示词", "Built-in prompt");

        Loc.Register(ScopeBuiltInKey, "内置", "Built-in");
        Loc.Register(ScopeGlobalKey, "全局", "Global");
        Loc.Register(ScopeProjectKey, "项目", "Project");

        Loc.Register(BadgeOverridesKey, "覆盖内置", "Overrides built-in");
        Loc.Register(BadgeAppendKey, "追加", "Append");
        Loc.Register(BadgeShadowedKey, "已被覆盖", "Overridden");
        Loc.Register(BadgeInvalidKey, "无效", "Invalid");

        Loc.Register(EmptyKey, "没有符合条件的模板", "No templates match");
        Loc.Register(SelectHintKey, "在左侧选择一项查看,或新建一个命令 / Agent。", "Select an item on the left, or create a command or agent.");
        Loc.Register(NewKey, "新建", "New");
        Loc.Register(IdLabelKey, "标识", "Id");
        Loc.Register(KindLabelKey, "种类", "Kind");
        Loc.Register(ScopeLabelKey, "作用域", "Scope");
        Loc.Register(FileLabelKey, "文件", "File");
        Loc.Register(TextLabelKey, "内容(frontmatter + 正文)", "Content (frontmatter + body)");
        Loc.Register(BuiltInReadOnlyKey, "内置项只读。复制到全局或项目后可以修改。", "Built-ins are read-only. Copy it to global or project to edit it.");
        Loc.Register(AppendHintKey, "追加模式:frontmatter 写 mode: append,正文接在下层同名模板之后", "Append mode: write mode: append in the frontmatter; the body follows the lower template of the same name");
        Loc.Register(ProblemsTitleKey, "问题", "Problems");
        Loc.Register(NoWorkspaceKey, "没有打开的会话,不能写项目作用域", "No session is open, so the project scope is unavailable");

        Loc.Register(CopyGlobalKey, "复制到全局", "Copy to global");
        Loc.Register(CopyProjectKey, "复制到项目", "Copy to project");
        Loc.Register(SaveKey, "保存", "Save");
        Loc.Register(CreateKey, "创建", "Create");
        Loc.Register(RevertKey, "还原", "Revert");
        Loc.Register(DeleteKey, "删除", "Delete");
        Loc.Register(DeleteConfirmKey, "确认删除", "Confirm delete");
        Loc.Register(DeleteCancelKey, "取消", "Cancel");

        Loc.Register(MsgSavedKey, "已保存;新会话起生效", "Saved; takes effect in new sessions");
        Loc.Register(MsgDeletedKey, "已删除,恢复为下层模板", "Deleted; the lower template applies again");
        Loc.Register(MsgCopiedKey, "已复制,可以在下面修改", "Copied; edit it below");
        Loc.Register(MsgConflictKey, "文件已在外部修改,请重新加载", "The file was changed outside Forge; reload it");
        Loc.Register(MsgExistsKey, "该作用域已有同名模板", "That scope already has a template with this id");
        Loc.Register(MsgSlotNewKey, "内置提示词不能新建,只能复制并覆盖", "Built-in prompts cannot be created, only copied and overridden");
        Loc.Register(MsgInvalidIdKey, "标识只能是 1-64 位小写字母、数字、点、下划线或连字符,以字母或数字开头", "The id must be 1-64 characters of a-z, 0-9, dot, underscore or hyphen, starting with a letter or digit");
        Loc.Register(MsgInvalidKey, "内容有问题,未保存", "The content has problems; nothing was saved");
        Loc.Register(MsgReadOnlyKey, "这个位置不可写", "This location is read-only");
        Loc.Register(MsgGoneKey, "文件已不存在", "The file no longer exists");
        Loc.Register(MsgFailedKey, "操作失败:{0}", "The operation failed: {0}");
        Loc.Register(MsgUnsavedKey, "有未保存的修改;再点一次放弃修改", "There are unsaved changes; click again to discard them");

        Loc.Register(ProblemInvalidIdKey, "标识不合法", "The id is not valid");
        Loc.Register(ProblemUnclosedFrontmatterKey, "frontmatter 的 --- 没有结束", "The frontmatter --- block is never closed");
        Loc.Register(ProblemUnknownKeyKey, "未知的 frontmatter 键:{0}", "Unknown frontmatter key: {0}");
        Loc.Register(ProblemInvalidValueKey, "frontmatter 键“{0}”的值无法识别", "The value of frontmatter key '{0}' is not valid");
        Loc.Register(ProblemEmptyBodyKey, "正文为空", "The body is empty");
        Loc.Register(ProblemUnknownSlotKey, "不是内置提示词的 id,内置提示词只能覆盖不能新增", "Not a built-in prompt id; built-in prompts can be overridden, not added");
        Loc.Register(ProblemUnknownPlaceholderKey, "这个提示词没有占位符 {{{0}}}", "This prompt has no placeholder {{{0}}}");
        Loc.Register(ProblemTooLargeKey, "文件超过 256 KiB", "The file is larger than 256 KiB");
        Loc.Register(ProblemUnreadableKey, "无法读取文件:{0}", "The file could not be read: {0}");
    }
}
