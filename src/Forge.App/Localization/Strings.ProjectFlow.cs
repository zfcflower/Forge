using System.Runtime.CompilerServices;

namespace Forge.App.Localization;

/// <summary>
/// The shell's project flows (wave W9): the add-project picker that offers "existing folder / clone / new project", and the
/// failure lines those flows report. The dialog's own texts live in <see cref="ProjectCreationStrings"/> and the first-run
/// wizard's in <see cref="OnboardingStrings"/>; this table is only what the shell says around them, so no two files of the
/// wave share a key prefix.
/// <para>Keys: <c>projectFlow.*</c>.</para>
/// </summary>
internal static class ProjectFlowStrings
{
    // ---------------------------------------------------------------- the projects settings section (registered by the wave's composition module)

    public const string SectionTitleKey = "projectFlow.sectionTitle";

    // ---------------------------------------------------------------- the add-project picker

    public const string AddTitleKey = "projectFlow.addTitle";
    public const string AddQuestionKey = "projectFlow.addQuestion";
    public const string AddExistingKey = "projectFlow.addExisting";
    public const string AddCloneKey = "projectFlow.addClone";
    public const string AddCreateKey = "projectFlow.addCreate";

    // ---------------------------------------------------------------- failures

    public const string AddFailedTitleKey = "projectFlow.addFailedTitle";
    public const string AddFailedKey = "projectFlow.addFailed";

    [ModuleInitializer]
    internal static void Initialize() => Register();

    /// <summary>Idempotent: registering the same key with the same texts twice is a no-op.</summary>
    internal static void Register()
    {
        Loc.Register(SectionTitleKey, "项目", "Projects");
        Loc.Register(AddTitleKey, "添加项目", "Add a project");
        Loc.Register(AddQuestionKey, "这个项目从哪里来?", "Where does this project come from?");
        Loc.Register(AddExistingKey, "已有文件夹", "Existing folder");
        Loc.Register(AddCloneKey, "克隆仓库", "Clone a repository");
        Loc.Register(AddCreateKey, "新建项目", "New project");
        Loc.Register(AddFailedTitleKey, "无法添加项目", "Could not add the project");
        Loc.Register(AddFailedKey, "项目没有添加成功:{0}", "The project was not added: {0}");
    }

    // ---------------------------------------------------------------- accessors

    public static string SectionTitle => Loc.Instance[SectionTitleKey];

    public static string AddTitle => Loc.Instance[AddTitleKey];

    public static string AddQuestion => Loc.Instance[AddQuestionKey];

    public static string AddExisting => Loc.Instance[AddExistingKey];

    public static string AddClone => Loc.Instance[AddCloneKey];

    public static string AddCreate => Loc.Instance[AddCreateKey];

    public static string AddFailedTitle => Loc.Instance[AddFailedTitleKey];

    /// <summary>The failure line of a registration that threw; the reason is not translated.</summary>
    public static LocText AddFailed(string reason) => new(AddFailedKey, LocText.Literal(reason));
}
