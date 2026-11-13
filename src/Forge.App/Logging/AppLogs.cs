using Microsoft.Extensions.Logging;

namespace Forge.App.Logging;

/// <summary>One logger per diagnostic category; the category is the name the old <c>[tag]</c> prefix carried.</summary>
internal static class AppLogs
{
    public static readonly ILogger Appearance = AppLog.Create("appearance");

    public static readonly ILogger Approval = AppLog.Create("approval");

    public static readonly ILogger ApprovalRules = AppLog.Create("approval-rules");

    public static readonly ILogger Crash = AppLog.Create("crash");

    public static readonly ILogger Automation = AppLog.Create("automation");

    public static readonly ILogger Branches = AppLog.Create("branches");

    public static readonly ILogger Changes = AppLog.Create("changes");

    public static readonly ILogger Checkpoint = AppLog.Create("checkpoint");

    public static readonly ILogger Ci = AppLog.Create("ci");

    public static readonly ILogger Composer = AppLog.Create("composer");

    public static readonly ILogger Diff = AppLog.Create("diff");

    public static readonly ILogger Editor = AppLog.Create("editor");

    public static readonly ILogger File = AppLog.Create("file");

    public static readonly ILogger Fork = AppLog.Create("fork");

    public static readonly ILogger Handover = AppLog.Create("handover");

    public static readonly ILogger Housekeeping = AppLog.Create("housekeeping");

    public static readonly ILogger Instruments = AppLog.Create("instruments");

    public static readonly ILogger Layout = AppLog.Create("layout");

    public static readonly ILogger Lifecycle = AppLog.Create("lifecycle");

    public static readonly ILogger Loc = AppLog.Create("loc");

    public static readonly ILogger Markdown = AppLog.Create("markdown");

    public static readonly ILogger Menu = AppLog.Create("menu");

    public static readonly ILogger ModelCatalog = AppLog.Create("model-catalog");

    public static readonly ILogger NewBranch = AppLog.Create("new-branch");

    public static readonly ILogger Notify = AppLog.Create("notify");

    public static readonly ILogger Preferences = AppLog.Create("preferences");

    public static readonly ILogger Prompts = AppLog.Create("prompts");

    public static readonly ILogger Providers = AppLog.Create("providers");

    public static readonly ILogger Review = AppLog.Create("review");

    public static readonly ILogger Session = AppLog.Create("session");

    public static readonly ILogger Settings = AppLog.Create("settings");

    public static readonly ILogger Shell = AppLog.Create("shell");

    public static readonly ILogger Ship = AppLog.Create("ship");

    public static readonly ILogger StepCommit = AppLog.Create("step-commit");

    public static readonly ILogger Terminal = AppLog.Create("terminal");

    public static readonly ILogger Toast = AppLog.Create("toast");

    public static readonly ILogger Tree = AppLog.Create("tree");

    public static readonly ILogger Turn = AppLog.Create("turn");

    public static readonly ILogger Usage = AppLog.Create("usage");

    public static readonly ILogger Workbench = AppLog.Create("workbench");

    public static readonly ILogger Worktree = AppLog.Create("worktree");
}
