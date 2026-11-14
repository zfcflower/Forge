using System.Runtime.CompilerServices;
using Forge.App.Services;
using Forge.Workspace;

namespace Forge.App.Localization;

/// <summary>
/// The project-creation texts (wave W9): the clone/new-repository dialog and the <c>projects</c> settings section. The
/// shell's own picker and failure lines live in <see cref="ProjectFlowStrings"/>; this table is only what the dialog and
/// the section themselves show, so no two files of the wave share a key prefix.
/// <para>Keys: <c>projectCreate.*</c> for the dialog and <c>settings.projects.*</c> for the settings section. Each key has
/// a Chinese and an English text; parameterized texts are exposed as typed accessor methods (formatted through
/// <see cref="Loc.Format"/>), parameterless ones as properties that read <see cref="Loc.Instance"/>, so callers never
/// cache a resolved string across a language switch.</para>
/// <para>Text the service hands back inside a <see cref="ProjectCreationResult"/> (why a name or a URL was refused, why a
/// run failed) is exposed as a <see cref="LocText"/> — the result outlives the call and must re-render on a language
/// switch.</para>
/// </summary>
internal static class ProjectCreationStrings
{
    // ---------------------------------------------------------------- the dialog: titles and one-sentence intros

    public const string CloneTitleKey = "projectCreate.cloneTitle";
    public const string CloneIntroKey = "projectCreate.cloneIntro";
    public const string CreateTitleKey = "projectCreate.createTitle";
    public const string CreateIntroKey = "projectCreate.createIntro";

    // ---------------------------------------------------------------- the dialog: inputs

    public const string UrlLabelKey = "projectCreate.urlLabel";
    public const string UrlPlaceholderKey = "projectCreate.urlPlaceholder";
    public const string ParentLabelKey = "projectCreate.parentLabel";
    public const string BrowseKey = "projectCreate.browse";
    public const string PickerTitleKey = "projectCreate.pickerTitle";
    public const string NameLabelKey = "projectCreate.nameLabel";
    public const string NamePlaceholderKey = "projectCreate.namePlaceholder";
    public const string DestinationLabelKey = "projectCreate.destinationLabel";

    // ---------------------------------------------------------------- the dialog: footer buttons and run states

    public const string CancelKey = "projectCreate.cancel";
    public const string StopKey = "projectCreate.stop";
    public const string CloneStartKey = "projectCreate.cloneStart";
    public const string CreateStartKey = "projectCreate.createStart";
    public const string CancelledKey = "projectCreate.cancelled";

    // ---------------------------------------------------------------- the dialog: hints (why the input cannot start)

    public const string ParentMissingKey = "projectCreate.hint.parentMissing";
    public const string NameEmptyKey = "projectCreate.hint.nameEmpty";
    public const string NameInvalidKey = "projectCreate.hint.nameInvalid";
    public const string NameReservedKey = "projectCreate.hint.nameReserved";
    public const string NameTrailingKey = "projectCreate.hint.nameTrailing";
    public const string NameNotSegmentKey = "projectCreate.hint.nameNotSegment";
    public const string NameTooLongKey = "projectCreate.hint.nameTooLong";
    public const string NameExistsKey = "projectCreate.hint.nameExists";
    public const string UrlNotUsableKey = "projectCreate.hint.url";
    public const string UrlPasswordKey = "projectCreate.hint.urlPassword";
    public const string ParentRelativeKey = "projectCreate.hint.parentRelative";

    // ---------------------------------------------------------------- the dialog: clone progress (git's own phase names)

    public const string PhaseConnectingKey = "projectCreate.phase.connecting";
    public const string PhaseEnumeratingKey = "projectCreate.phase.enumerating";
    public const string PhaseCountingKey = "projectCreate.phase.counting";
    public const string PhaseCompressingKey = "projectCreate.phase.compressing";
    public const string PhaseReceivingKey = "projectCreate.phase.receiving";
    public const string PhaseResolvingKey = "projectCreate.phase.resolving";
    public const string PhaseUpdatingKey = "projectCreate.phase.updating";
    public const string ProgressPercentDetailKey = "projectCreate.progress.percentDetail";
    public const string ProgressPercentKey = "projectCreate.progress.percent";
    public const string ProgressDetailKey = "projectCreate.progress.detail";

    // ---------------------------------------------------------------- the `projects` settings section

    public const string TimeoutLabelKey = "settings.projects.timeoutLabel";
    public const string TimeoutHintKey = "settings.projects.timeoutHint";
    public const string DirectoryLabelKey = "settings.projects.directoryLabel";
    public const string DirectoryHintKey = "settings.projects.directoryHint";
    public const string SettingsBrowseKey = "settings.projects.browse";
    public const string SettingsClearKey = "settings.projects.clear";
    public const string SettingsPickerTitleKey = "settings.projects.pickerTitle";

    /// <summary>Registers this table before any code of the assembly runs (module initializer, AOT safe).</summary>
    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(CloneTitleKey, "克隆仓库", "Clone a repository");
        Loc.Register(CloneIntroKey, "把远程仓库克隆到一个新文件夹。", "Clones a remote repository into a new folder.");
        Loc.Register(CreateTitleKey, "新建项目", "New project");
        Loc.Register(CreateIntroKey, "创建一个空文件夹,并在其中初始化 git 仓库。", "Creates an empty folder and initializes a git repository in it.");

        Loc.Register(UrlLabelKey, "远程地址", "Remote address");
        Loc.Register(UrlPlaceholderKey, "https://… 或 git@… 形式的仓库地址", "A repository address like https://… or git@…");
        Loc.Register(ParentLabelKey, "父目录", "Parent directory");
        Loc.Register(BrowseKey, "浏览…", "Browse…");
        Loc.Register(PickerTitleKey, "选择父目录", "Choose the parent directory");
        Loc.Register(NameLabelKey, "项目名称", "Project name");
        Loc.Register(NamePlaceholderKey, "新文件夹的名称", "The new folder's name");
        Loc.Register(DestinationLabelKey, "目标路径", "Destination");

        Loc.Register(CancelKey, "取消", "Cancel");
        Loc.Register(StopKey, "停止", "Stop");
        Loc.Register(CloneStartKey, "克隆", "Clone");
        Loc.Register(CreateStartKey, "创建", "Create");
        Loc.Register(CancelledKey, "已取消。", "Cancelled.");

        Loc.Register(ParentMissingKey, "请先选择项目所在的父目录。", "Choose the directory the project is created in first.");
        Loc.Register(NameEmptyKey, "请输入项目名称。", "Enter a project name.");
        Loc.Register(NameInvalidKey, "名称包含文件夹名不允许的字符。", "The name holds characters a folder name cannot.");
        Loc.Register(NameReservedKey, "这是 Windows 保留的设备名,不能用作文件夹名。", "That is a Windows reserved device name; it cannot be a folder.");
        Loc.Register(NameTrailingKey, "名称不能以空格或点结尾。", "The name cannot end with a space or a dot.");
        Loc.Register(NameNotSegmentKey, "名称只能是单独一段路径,不能包含路径分隔。", "The name is a single path segment, not a path.");
        Loc.Register(NameTooLongKey, "名称太长,Windows 无法创建。", "The name is too long for Windows to create.");
        Loc.Register(NameExistsKey, "同名文件夹已存在且不为空。", "A folder with that name already exists and is not empty.");
        Loc.Register(UrlPasswordKey, "地址里带有密码或令牌;请去掉 “用户名:密码@” 部分,git 会通过凭据管理器询问。", "The address carries a password or token; remove the \"user:password@\" part and git asks through the credential manager.");
        Loc.Register(ParentRelativeKey, "父目录必须是完整的绝对路径。", "The parent directory must be a full, absolute path.");
        Loc.Register(UrlNotUsableKey, "请输入完整的远程地址(https:// 或 git@ 开头)。", "Enter a complete remote address (starting with https:// or git@).");

        Loc.Register(PhaseConnectingKey, "连接远程", "Connecting");
        Loc.Register(PhaseEnumeratingKey, "枚举对象", "Enumerating objects");
        Loc.Register(PhaseCountingKey, "统计对象", "Counting objects");
        Loc.Register(PhaseCompressingKey, "压缩对象", "Compressing objects");
        Loc.Register(PhaseReceivingKey, "接收对象", "Receiving objects");
        Loc.Register(PhaseResolvingKey, "解析增量", "Resolving deltas");
        Loc.Register(PhaseUpdatingKey, "检出文件", "Updating files");
        Loc.Register(ProgressPercentDetailKey, "{0} {1}% · {2}", "{0} {1}% · {2}");
        Loc.Register(ProgressPercentKey, "{0} {1}%", "{0} {1}%");
        Loc.Register(ProgressDetailKey, "{0} · {1}", "{0} · {1}");

        Loc.Register(TimeoutLabelKey, "克隆超时(秒)", "Clone timeout (s)");
        Loc.Register(TimeoutHintKey, "超过这个时长未完成的克隆会被中止;0 表示不限制。", "A clone that runs longer is stopped; 0 means no limit.");
        Loc.Register(DirectoryLabelKey, "默认目录", "Default directory");
        Loc.Register(DirectoryHintKey, "克隆或新建项目时项目文件夹的默认位置;留空时在常规工作区所在的目录创建。", "Where a cloned or new project folder goes by default; when empty, projects are created next to the general workspace.");
        Loc.Register(SettingsBrowseKey, "浏览…", "Browse…");
        Loc.Register(SettingsClearKey, "清除", "Clear");
        Loc.Register(SettingsPickerTitleKey, "选择默认目录", "Choose the default directory");
    }

    // ---------------------------------------------------------------- accessors: titles and intros

    public static string CloneTitle => Loc.Instance[CloneTitleKey];

    public static string CloneIntro => Loc.Instance[CloneIntroKey];

    public static string CreateTitle => Loc.Instance[CreateTitleKey];

    public static string CreateIntro => Loc.Instance[CreateIntroKey];

    // ---------------------------------------------------------------- accessors: inputs

    public static string UrlLabel => Loc.Instance[UrlLabelKey];

    public static string UrlPlaceholder => Loc.Instance[UrlPlaceholderKey];

    public static string ParentLabel => Loc.Instance[ParentLabelKey];

    public static string Browse => Loc.Instance[BrowseKey];

    /// <summary>The folder picker's own window title; not part of this app's chrome, but a text the user reads.</summary>
    public static string PickerTitle => Loc.Instance[PickerTitleKey];

    public static string NameLabel => Loc.Instance[NameLabelKey];

    public static string NamePlaceholder => Loc.Instance[NamePlaceholderKey];

    public static string DestinationLabel => Loc.Instance[DestinationLabelKey];

    // ---------------------------------------------------------------- accessors: footer buttons and run states

    public static string Cancel => Loc.Instance[CancelKey];

    public static string Stop => Loc.Instance[StopKey];

    public static string CloneStart => Loc.Instance[CloneStartKey];

    public static string CreateStart => Loc.Instance[CreateStartKey];

    /// <summary>The error line of a creation the user stopped; shown where a failure would be, so both read the same.</summary>
    public static LocText Cancelled => new(CancelledKey);

    // ---------------------------------------------------------------- accessors: hints

    /// <summary>Why a request carries no usable parent directory; the service reports it, the dialog shows it live.</summary>
    public static LocText ParentDirectoryMissing => new(ParentMissingKey);

    /// <summary>Why a clone's URL cannot be handed to git; the service reports it, the dialog shows it live.</summary>
    public static LocText UrlNotUsable => new(UrlNotUsableKey);

    /// <summary>Why a clone's URL is refused when it embeds a password: it would sit on git's command line and in its errors.</summary>
    public static LocText UrlCarriesPassword => new(UrlPasswordKey);

    /// <summary>Why a typed parent directory is refused when it is relative: it would resolve against the process's directory.</summary>
    public static LocText ParentDirectoryNotAbsolute => new(ParentRelativeKey);

    /// <summary>The sentence for one refused name, in the order <see cref="ProjectNameProblem"/> checks.</summary>
    public static LocText NameProblem(ProjectNameProblem problem) => problem switch
    {
        ProjectNameProblem.Empty => new(NameEmptyKey),
        ProjectNameProblem.InvalidCharacters => new(NameInvalidKey),
        ProjectNameProblem.NotASegment => new(NameNotSegmentKey),
        ProjectNameProblem.TooLong => new(NameTooLongKey),
        ProjectNameProblem.TrailingDotOrSpace => new(NameTrailingKey),
        ProjectNameProblem.ReservedName => new(NameReservedKey),
        ProjectNameProblem.Exists => new(NameExistsKey),
        _ => new(NameEmptyKey),
    };

    // ---------------------------------------------------------------- accessors: clone progress

    /// <summary>The localized name of one phase git reports; the progress line is built from these.</summary>
    public static string PhaseName(GitClonePhase phase) => phase switch
    {
        GitClonePhase.Connecting => Loc.Instance[PhaseConnectingKey],
        GitClonePhase.Enumerating => Loc.Instance[PhaseEnumeratingKey],
        GitClonePhase.Counting => Loc.Instance[PhaseCountingKey],
        GitClonePhase.Compressing => Loc.Instance[PhaseCompressingKey],
        GitClonePhase.Receiving => Loc.Instance[PhaseReceivingKey],
        GitClonePhase.Resolving => Loc.Instance[PhaseResolvingKey],
        GitClonePhase.Updating => Loc.Instance[PhaseUpdatingKey],
        _ => Loc.Instance[PhaseReceivingKey],
    };

    /// <summary>
    /// One progress line: the localized phase, its own percentage and git's detail when it printed either
    /// ("Receiving objects 47% · 3.82 MiB/s"), the phase alone when it printed neither.
    /// </summary>
    public static string ProgressLine(GitCloneProgress progress)
    {
        var phase = PhaseName(progress.Phase);
        if (progress.Percent is not { } percent)
        {
            return progress.Detail.Length == 0 ? phase : Loc.Instance.Format(ProgressDetailKey, phase, progress.Detail);
        }

        return progress.Detail.Length == 0
            ? Loc.Instance.Format(ProgressPercentKey, phase, TextFormat.Number(percent))
            : Loc.Instance.Format(ProgressPercentDetailKey, phase, TextFormat.Number(percent), progress.Detail);
    }

    // ---------------------------------------------------------------- accessors: the `projects` settings section

    public static string TimeoutLabel => Loc.Instance[TimeoutLabelKey];

    public static string TimeoutHint => Loc.Instance[TimeoutHintKey];

    public static string DirectoryLabel => Loc.Instance[DirectoryLabelKey];

    public static string DirectoryHint => Loc.Instance[DirectoryHintKey];

    public static string SettingsBrowse => Loc.Instance[SettingsBrowseKey];

    public static string SettingsClear => Loc.Instance[SettingsClearKey];

    /// <summary>The folder picker's own window title for the settings row; the picked folder becomes the new default.</summary>
    public static string SettingsPickerTitle => Loc.Instance[SettingsPickerTitleKey];
}
