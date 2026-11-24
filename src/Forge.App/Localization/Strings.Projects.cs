using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The project region's table: the project tree, its context menus, the add-project and git-init dialogs, and default session titles.
/// <para>Keys: <c>project.*</c>. Each key has a Chinese and an English text; parameterized texts are exposed as typed accessor
/// methods (formatted through <see cref="Loc.Format"/>), parameterless ones as properties that read
/// <see cref="Loc.Instance"/>, so callers never cache a resolved string across a language switch.</para>
/// <para>Text that is stored and shown later (the tree's status line, which survives until the next refresh) is exposed as a
/// <see cref="LocText"/> instead, so it re-renders when the language changes. Key constants carry a <c>Key</c> suffix where an
/// accessor of the same name exists.</para>
/// </summary>
internal static class ProjectStrings
{
    // ---------------------------------------------------------------- the general section and the tree rows
    public const string GeneralSectionKey = "project.generalSection";
    public const string EmptyProjectsKey = "project.empty";
    public const string ProjectCountKeyBase = "project.count";
    public const string HistoryTitleKey = "project.history";
    public const string ArchivedSubtitleKey = "project.archivedSubtitle";
    public const string RunningLabelKey = "project.running";
    public const string TreeNameKey = "project.treeName";
    public const string MainCheckoutDetachedKey = "project.mainCheckout.detached";
    public const string PairRemoteTipKey = "project.pairRemote.tip";

    // ---------------------------------------------------------------- the new-branch dialog
    public const string NewBranchTitleKey = "project.newBranch.title";
    public const string NewBranchBaseLabelKey = "project.newBranch.baseLabel";
    public const string NewBranchNameLabelKey = "project.newBranch.nameLabel";
    public const string NewBranchNamePlaceholderKey = "project.newBranch.namePlaceholder";
    public const string NewBranchCreateKey = "project.newBranch.create";
    public const string NewBranchCancelKey = "project.newBranch.cancel";
    public const string NewBranchNameRequiredKey = "project.newBranch.nameRequired";
    public const string NewBranchNameInvalidKey = "project.newBranch.nameInvalid";
    public const string NewBranchNameTakenKey = "project.newBranch.nameTaken";
    public const string NewBranchLoadingKey = "project.newBranch.loading";
    public const string NewBranchNoBaseKey = "project.newBranch.noBase";
    public const string NewBranchBasePlaceholderKey = "project.newBranch.basePlaceholder";

    // ---------------------------------------------------------------- default session titles (generated once, at creation)
    public const string GeneralSessionTitleKey = "project.sessionTitle.general";
    public const string WorktreeSessionTitleKey = "project.sessionTitle.worktree";

    // ---------------------------------------------------------------- rename dialogs
    public const string RenameSessionTitleKey = "project.renameSessionTitle";
    public const string RenameProjectTitleKey = "project.renameProjectTitle";

    // ---------------------------------------------------------------- failures and empty states
    public const string GeneralWorkspaceFailedKey = "project.generalWorkspaceFailed";
    public const string TreeFailedKey = "project.treeFailed";
    public const string NotAGitRepositoryKey = "project.notAGitRepository";
    public const string GeneralWorkspaceMissingKey = "project.generalWorkspaceMissing";

    // ---------------------------------------------------------------- context menus, buttons and their automation names
    public const string NewSessionKey = "project.newSession";
    public const string NewSessionTooltipKey = "project.newSessionTooltip";
    public const string RenameKey = "project.rename";
    public const string ArchiveKey = "project.archive";
    public const string RevealInExplorerKey = "project.reveal";
    public const string OpenInVsCodeKey = "project.openVscode";
    public const string PromoteToMainKey = "project.promoteToMain";
    public const string PromotedToMainKey = "project.promotedToMain";
    public const string RemoveProjectKey = "project.remove";
    public const string OpenKey = "project.open";
    public const string OpenSessionNameKey = "project.openSessionName";
    public const string RefreshKey = "project.refresh";
    public const string RefreshProjectsNameKey = "project.refreshProjectsName";
    public const string AddProjectKey = "project.addProject";
    public const string AddProjectNameKey = "project.addProjectName";
    public const string AddProjectTooltipKey = "project.addProjectTooltip";
    /// <summary>The project row's "new workspace…" (lifecycle 5.4 rule 5): one key for its menu entry and its trailing button.</summary>
    public const string NewWorkspaceKey = "project.newWorkspace";
    public const string NewGeneralSessionTooltipKey = "project.newGeneralSession";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(GeneralSectionKey, "常规对话", "General chat");
        Loc.Register(EmptyProjectsKey, "还没有项目。用“+”添加一个 git 仓库。", "No projects yet. Use “+” to add a git repository.");
        Loc.Register(ProjectCountKeyBase + ".one", "共 {0} 个区域", "{0} area");
        Loc.Register(ProjectCountKeyBase + ".other", "共 {0} 个区域", "{0} areas");
        Loc.Register(HistoryTitleKey, "历史 ({0})", "History ({0})");
        Loc.Register(ArchivedSubtitleKey, "已归档 · 可恢复会话", "Archived · restore to continue");
        Loc.Register(RunningLabelKey, "运行中", "Running");
        Loc.Register(TreeNameKey, "项目树", "Project tree");

        Loc.Register(GeneralSessionTitleKey, "常规 {0}", "General {0}");
        Loc.Register(WorktreeSessionTitleKey, "会话 {0}", "Session {0}");

        Loc.Register(RenameSessionTitleKey, "重命名会话", "Rename session");
        Loc.Register(RenameProjectTitleKey, "重命名项目", "Rename project");

        Loc.Register(GeneralWorkspaceFailedKey, "通用工作区注册失败：{0}", "Registering the general workspace failed: {0}");
        Loc.Register(TreeFailedKey, "项目树读取失败：{0}", "Reading the project tree failed: {0}");
        Loc.Register(NotAGitRepositoryKey, "该文件夹不是 git 仓库。", "This folder is not a git repository.");
        Loc.Register(GeneralWorkspaceMissingKey, "常规工作区尚未注册。", "The general workspace is not registered yet.");

        Loc.Register(NewSessionKey, "新建会话", "New session");
        Loc.Register(NewSessionTooltipKey, "新建会话", "New session");
        Loc.Register(RenameKey, "重命名", "Rename");
        Loc.Register(ArchiveKey, "归档", "Archive");
        Loc.Register(RevealInExplorerKey, "在资源管理器中打开", "Reveal in Explorer");
        Loc.Register(OpenInVsCodeKey, "在 VS Code 中打开", "Open in VS Code");
        Loc.Register(PromoteToMainKey, "晋升为主会话", "Promote to main session");
        Loc.Register(PromotedToMainKey, "“{0}”现在是这个工作区的主会话。", "\"{0}\" is the main session of this workspace now.");
        Loc.Register(RemoveProjectKey, "移除项目(仅取消注册)", "Remove project (unregisters only)");
        Loc.Register(OpenKey, "打开", "Open");
        Loc.Register(OpenSessionNameKey, "打开会话", "Open session");
        Loc.Register(RefreshKey, "刷新", "Refresh");
        Loc.Register(RefreshProjectsNameKey, "刷新项目", "Refresh projects");
        Loc.Register(AddProjectKey, "+ 添加项目", "+ Add project");
        Loc.Register(AddProjectNameKey, "添加项目", "Add project");
        Loc.Register(AddProjectTooltipKey, "添加一个 git 仓库作为项目", "Add a git repository as a project");
        Loc.Register(NewWorkspaceKey, "新建工作区…", "New workspace…");
        Loc.Register(NewGeneralSessionTooltipKey, "新建常规对话", "New general chat");
        Loc.Register(MainCheckoutDetachedKey, "游离 HEAD", "Detached HEAD");

        Loc.Register(NewBranchTitleKey, "新建工作区", "New workspace");
        Loc.Register(NewBranchBaseLabelKey, "基于分支", "Start from");
        Loc.Register(NewBranchNameLabelKey, "分支名称", "Branch name");
        Loc.Register(NewBranchNamePlaceholderKey, "分支名称", "Branch name");
        Loc.Register(NewBranchCreateKey, "创建", "Create");
        Loc.Register(NewBranchCancelKey, "取消", "Cancel");
        Loc.Register(NewBranchNameRequiredKey, "请输入分支名称", "Enter a branch name");
        Loc.Register(NewBranchNameInvalidKey, "不是合法的分支名", "Not a valid branch name");
        Loc.Register(NewBranchNameTakenKey, "已有同名分支", "A branch with that name already exists");
        Loc.Register(NewBranchLoadingKey, "正在读取分支…", "Loading branches…");
        Loc.Register(NewBranchNoBaseKey, "没有可用的分支", "No branches available");
        Loc.Register(NewBranchBasePlaceholderKey, "选择分支", "Choose a branch");
    }

    // ---------------------------------------------------------------- accessors: the general section and the tree rows

    /// <summary>The top row of the tree that holds the sessions of the general workspace.</summary>
    public static string GeneralSection => Loc.Instance[GeneralSectionKey];

    /// <summary>The tree's status line while no project is registered; shown until the next refresh, so it stays a key.</summary>
    public static LocText EmptyProjects => new(EmptyProjectsKey);

    /// <summary>How many regions the tree has; a count-dependent English noun, so the key splits into <c>.one</c>/<c>.other</c>.</summary>
    public static string ProjectCount(long count) => Loc.Instance.FormatPlural(ProjectCountKeyBase, count);

    /// <summary>The collapsible history group of a worktree, with its archived session count.</summary>
    public static string HistoryTitle(long count) => Loc.Instance.Format(HistoryTitleKey, TextFormat.Number(count));

    /// <summary>The subtitle of an archived session row.</summary>
    public static string ArchivedSubtitle => Loc.Instance[ArchivedSubtitleKey];

    /// <summary>The label beside the running dot of a session row.</summary>
    public static string RunningLabel => Loc.Instance[RunningLabelKey];

    /// <summary>The accessible name of the tree itself.</summary>
    public static string TreeName => Loc.Instance[TreeNameKey];

    /// <summary>The main checkout row's label when the repository's HEAD is detached from every branch.</summary>
    public static string MainCheckoutDetached => Loc.Instance[MainCheckoutDetachedKey];

    // ---------------------------------------------------------------- accessors: the new-branch dialog

    public static string NewBranchTitle => Loc.Instance[NewBranchTitleKey];

    public static string NewBranchBaseLabel => Loc.Instance[NewBranchBaseLabelKey];

    public static string NewBranchNameLabel => Loc.Instance[NewBranchNameLabelKey];

    public static string NewBranchNamePlaceholder => Loc.Instance[NewBranchNamePlaceholderKey];

    public static string NewBranchCreate => Loc.Instance[NewBranchCreateKey];

    public static string NewBranchCancel => Loc.Instance[NewBranchCancelKey];

    public static string NewBranchNameRequired => Loc.Instance[NewBranchNameRequiredKey];

    public static string NewBranchNameInvalid => Loc.Instance[NewBranchNameInvalidKey];

    public static string NewBranchNameTaken => Loc.Instance[NewBranchNameTakenKey];

    public static string NewBranchLoading => Loc.Instance[NewBranchLoadingKey];

    public static string NewBranchNoBase => Loc.Instance[NewBranchNoBaseKey];

    public static string NewBranchBasePlaceholder => Loc.Instance[NewBranchBasePlaceholderKey];

    // ---------------------------------------------------------------- accessors: default session titles

    /// <summary>A new general-chat session's title, in the language current at creation; the stored title is user data afterwards.</summary>
    public static string GeneralSessionTitle() =>
        Loc.Instance.Format(GeneralSessionTitleKey, TextFormat.ShortStamp(DateTimeOffset.Now));

    /// <summary>A new worktree session's title, in the language current at creation; the stored title is user data afterwards.</summary>
    public static string WorktreeSessionTitle() =>
        Loc.Instance.Format(WorktreeSessionTitleKey, TextFormat.ShortStamp(DateTimeOffset.Now));

    // ---------------------------------------------------------------- accessors: rename dialogs

    public static string RenameSessionTitle => Loc.Instance[RenameSessionTitleKey];

    public static string RenameProjectTitle => Loc.Instance[RenameProjectTitleKey];

    // ---------------------------------------------------------------- accessors: failures and empty states

    /// <summary>The status line after the general workspace failed to register; the message is not translated.</summary>
    public static LocText GeneralWorkspaceFailed(string message) =>
        new(GeneralWorkspaceFailedKey, LocText.Literal(message));

    /// <summary>The status line after the tree failed to read; the message is not translated.</summary>
    public static LocText TreeFailed(string message) => new(TreeFailedKey, LocText.Literal(message));

    /// <summary>The exception message of a folder that is not a repository.</summary>
    public static string NotAGitRepository => Loc.Instance[NotAGitRepositoryKey];

    /// <summary>The exception message of a missing general workspace.</summary>
    public static string GeneralWorkspaceMissing => Loc.Instance[GeneralWorkspaceMissingKey];

    // ---------------------------------------------------------------- accessors: context menus, buttons and automation names

    public static string NewSession => Loc.Instance[NewSessionKey];

    public static string NewSessionTooltip => Loc.Instance[NewSessionTooltipKey];

    public static string Rename => Loc.Instance[RenameKey];

    public static string Archive => Loc.Instance[ArchiveKey];

    public static string RevealInExplorer => Loc.Instance[RevealInExplorerKey];

    public static string OpenInVsCode => Loc.Instance[OpenInVsCodeKey];

    public static string PromoteToMain => Loc.Instance[PromoteToMainKey];

    /// <summary>The tree's status line after an archive handed the workspace to another session.</summary>
    public static string PromotedToMain(string session) => Loc.Instance.Format(PromotedToMainKey, session);

    public static string RemoveProject => Loc.Instance[RemoveProjectKey];

    public static string Open => Loc.Instance[OpenKey];

    public static string OpenSessionName => Loc.Instance[OpenSessionNameKey];

    public static string Refresh => Loc.Instance[RefreshKey];

    public static string RefreshProjectsName => Loc.Instance[RefreshProjectsNameKey];

    public static string AddProject => Loc.Instance[AddProjectKey];

    public static string AddProjectName => Loc.Instance[AddProjectNameKey];

    public static string AddProjectTooltip => Loc.Instance[AddProjectTooltipKey];

    public static string NewWorkspace => Loc.Instance[NewWorkspaceKey];

    public static string NewGeneralSessionTooltip => Loc.Instance[NewGeneralSessionTooltipKey];
}


/// <summary>The "pair a remote" flow of a project row (<c>project.pairRemote.*</c>).</summary>
internal static class PairingStrings
{
    public const string TitleKey = "project.pairRemote.title";
    public const string QuestionKey = "project.pairRemote.question";
    public const string ByUrlKey = "project.pairRemote.byUrl";
    public const string ByGitHubKey = "project.pairRemote.byGitHub";
    public const string CancelKey = "project.pairRemote.cancel";
    public const string UrlPromptKey = "project.pairRemote.urlPrompt";
    public const string NamePromptKey = "project.pairRemote.namePrompt";
    public const string VisibilityTitleKey = "project.pairRemote.visibilityTitle";
    public const string VisibilityQuestionKey = "project.pairRemote.visibilityQuestion";
    public const string PrivateKey = "project.pairRemote.private";
    public const string PublicKey = "project.pairRemote.public";
    public const string FailedKey = "project.pairRemote.failed";
    public const string DoneTitleKey = "project.pairRemote.doneTitle";
    public const string DoneKey = "project.pairRemote.done";

    public static string Title => Loc.Instance[TitleKey];

    public static string Question(string project) => Loc.Instance.Format(QuestionKey, project);

    public static string ByUrl => Loc.Instance[ByUrlKey];

    public static string ByGitHub => Loc.Instance[ByGitHubKey];

    public static string Cancel => Loc.Instance[CancelKey];

    public static string UrlPrompt => Loc.Instance[UrlPromptKey];

    public static string NamePrompt => Loc.Instance[NamePromptKey];

    public static string VisibilityTitle => Loc.Instance[VisibilityTitleKey];

    public static string VisibilityQuestion(string name) => Loc.Instance.Format(VisibilityQuestionKey, name);

    public static string Private => Loc.Instance[PrivateKey];

    public static string Public => Loc.Instance[PublicKey];

    public static string Failed(string reason) => Loc.Instance.Format(FailedKey, reason);

    public static LocText DoneTitle => new(DoneTitleKey);

    public static LocText Done(string project) => new(DoneKey, project);

    [ModuleInitializer]
    internal static void Register()
    {
        Loc.Register(TitleKey, "配对远程仓库", "Pair a remote repository");
        Loc.Register(
            QuestionKey,
            "项目「{0}」还没有远程仓库，推送和拉取请求都需要它。怎么配对？",
            "Project \"{0}\" has no remote repository yet; pushing and pull requests need one. How do you want to pair it?");
        Loc.Register(ByUrlKey, "输入远程地址", "Enter a remote URL");
        Loc.Register(ByGitHubKey, "在 GitHub 创建仓库", "Create a GitHub repository");
        Loc.Register(CancelKey, "取消", "Cancel");
        Loc.Register(UrlPromptKey, "远程仓库地址（会先用 git ls-remote 验证）", "Remote repository URL (verified with git ls-remote first)");
        Loc.Register(NamePromptKey, "GitHub 仓库名（可写成 所有者/名称）", "GitHub repository name (or owner/name)");
        Loc.Register(VisibilityTitleKey, "仓库可见性", "Repository visibility");
        Loc.Register(
            VisibilityQuestionKey,
            "用 gh 创建 GitHub 仓库「{0}」并设为 origin；只创建，不推送。",
            "Create the GitHub repository \"{0}\" with gh and set it as origin; nothing is pushed.");
        Loc.Register(PrivateKey, "私有", "Private");
        Loc.Register(PublicKey, "公开", "Public");
        Loc.Register(FailedKey, "配对失败，没有改动任何配置：{0}", "Pairing failed; nothing was changed: {0}");
        Loc.Register(DoneTitleKey, "已配对远程仓库", "Remote paired");
        Loc.Register(
            DoneKey,
            "项目「{0}」的 origin 已就绪。已开的会话从下一回合起提供拉取请求工具（提示缓存从该工具组起失效一次）。",
            "origin of \"{0}\" is set. Open sessions offer the pull request tools from their next turn (the prompt cache is lost once, from that tool group on).");
        Loc.Register(ProjectStrings.PairRemoteTipKey, "配对远程仓库", "Pair a remote repository");
    }
}
