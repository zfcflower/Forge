using Forge.Abstractions;
using Forge.Core.Prompts.Templates;
using Forge.Workspace;

namespace Forge.App.Prompts;

/// <summary>Ids of the prompt slots the app (not Core) owns. Their texts go to the model, so they are English and never localized.</summary>
internal static class AppPromptSlotIds
{
    /// <summary>Sent to a session the quota automation resumes. No placeholders.</summary>
    public const string QuotaContinue = "automations.quota.continue";

    /// <summary>The Scout task of the CI chain. Placeholders <c>{pr}</c>, <c>{branch}</c>, <c>{failures}</c>.</summary>
    public const string CiScout = "automations.ci.scout";

    /// <summary>The fix prompt of the CI chain. Placeholders <c>{pr}</c>, <c>{analysis}</c>.</summary>
    public const string CiFix = "automations.ci.fix";

    /// <summary>Prefix of the six <c>pr_workflow</c> phase slots: <c>pr.workflow.&lt;phase&gt;</c>.</summary>
    public const string PrWorkflowPrefix = "pr.workflow.";

    public static string PrWorkflow(string phase) => PrWorkflowPrefix + phase;
}

/// <summary>The app's compiled-in prompt texts. Kept here (not in a <c>Strings.*</c> table) because they are model input.</summary>
internal static class AppPromptTexts
{
    public const string QuotaContinue =
        "The model quota has been restored. Continue the previous task from where it stopped.";

    public const string CiScout =
        """
        The CI checks of PR #{pr} (branch {branch}) failed. The failed jobs and the tails of their logs follow.
        Investigate read-only: find the root cause (cite files and lines), decide whether it is a code problem, a test problem or an environment/flaky problem, and propose the smallest fix. Do not change any file.

        {failures}
        """;

    public const string CiFix =
        """
        The CI checks of PR #{pr} failed. Scout's analysis:

        {analysis}

        Fix the root cause and commit (do not push; the automation pushes). If it is a flaky or environment problem that needs no code change, say so and do not commit.
        """;

    /// <summary>Core's built-ins plus the pull request workflow phases and the automation prompts.</summary>
    public static PromptTemplateRegistry Registry { get; } = PromptTemplateRegistry.Core.With(AppTemplates());

    private static IEnumerable<BuiltInPromptTemplate> AppTemplates()
    {
        foreach (var phase in PrWorkflowText.Phases)
        {
            PrWorkflowText.TryGet(phase, out var text);
            yield return PromptTemplateRegistry.Slot(
                AppPromptSlotIds.PrWorkflow(phase),
                "pr_workflow: " + phase,
                "Guidance the pr_workflow tool returns for the '" + phase + "' phase of a pull request.",
                text);
        }

        yield return PromptTemplateRegistry.Slot(
            AppPromptSlotIds.QuotaContinue,
            "Quota resume",
            "Sent to a session when the model quota is restored and the automation resumes it.",
            QuotaContinue);
        yield return PromptTemplateRegistry.Slot(
            AppPromptSlotIds.CiScout,
            "CI failure: Scout task",
            "The read-only investigation task the CI automation gives Scout.",
            CiScout,
            "pr",
            "branch",
            "failures");
        yield return PromptTemplateRegistry.Slot(
            AppPromptSlotIds.CiFix,
            "CI failure: fix prompt",
            "Sent to the session after Scout's analysis to fix the failing checks.",
            CiFix,
            "pr",
            "analysis");
    }
}

/// <summary>
/// The app-wide prompt template store over <c>~/.forge</c> and the workspace's <c>.forge</c>. Every member does file IO,
/// so UI code calls it from the thread pool. <see cref="Changed"/> fires after a save or delete through this service (the
/// providers page re-lists its user agent rows); composers re-resolve when a new slash token starts, and sessions pick edits up when their agent
/// host is next built.
/// </summary>
internal sealed class PromptTemplateService
{
    public PromptTemplateService(string? userDirectory, PromptTemplateRegistry? registry = null)
    {
        Store = new PromptTemplateStore(registry ?? AppPromptTexts.Registry, userDirectory);
    }

    public PromptTemplateStore Store { get; }

    public event Action? Changed;

    public PromptTemplateSet Resolve(string? workspace) => Store.Resolve(workspace);

    public PromptTemplateSaveResult Save(PromptTemplateRef reference, string? workspace, string text, string? expectedRevision)
    {
        var result = Store.Save(reference, workspace, text, expectedRevision);
        if (result.Status == PromptTemplateSaveStatus.Saved)
        {
            Changed?.Invoke();
        }

        return result;
    }

    public PromptTemplateSaveStatus Delete(PromptTemplateRef reference, string? workspace, string expectedRevision)
    {
        var status = Store.Delete(reference, workspace, expectedRevision);
        if (status == PromptTemplateSaveStatus.Saved)
        {
            Changed?.Invoke();
        }

        return status;
    }

    /// <summary>The current text of a <c>pr_workflow</c> phase override for that working directory, or null to use the compiled-in one.</summary>
    public string? PrWorkflowOverride(string phase, string? workspace) =>
        Store.Resolve(workspace) is var set && set.IsSlotCustomized(AppPromptSlotIds.PrWorkflow(phase))
            ? set.FindSlot(AppPromptSlotIds.PrWorkflow(phase))
            : null;
}
