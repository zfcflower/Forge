using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Providers;

namespace Forge.App.Automations;

/// <summary>
/// <see cref="BuiltInAutomations.ModelCatalogRefresh"/> (daily at 05:00): refresh <c>model-catalog.json</c> from the
/// configured catalog address, then refresh every connected provider's own model list. The three entry points share one
/// service, and the trigger source decides how much work each one does:
/// <list type="bullet">
/// <item><c>Schedule</c>/<c>CatchUp</c> — the daily anchor: always asks the source (a 304 costs a few hundred bytes) and
/// always refreshes the provider lists.</item>
/// <item><c>Manual</c> — the automations page's "run now": the same work, but nothing at all when today's catalog is already
/// on disk.</item>
/// <item><c>Event</c> — the startup catch-up: the snapshot only, and only when today's is missing, because a provider list
/// call needs a credential per provider and the window is still starting.</item>
/// </list>
/// </summary>
internal sealed class ModelCatalogRefreshRunner(ModelCatalogRefreshService service) : IAutomationRunner
{
    public async Task<AutomationRunOutcome> RunAsync(IAutomationRunContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var mode = context.Run.Source switch
        {
            AutomationTriggerSource.Event => ModelCatalogRefreshMode.Startup,
            AutomationTriggerSource.Manual => ModelCatalogRefreshMode.Manual,
            _ => ModelCatalogRefreshMode.Scheduled,
        };

        var result = await service.RefreshAsync(mode, ct).ConfigureAwait(false);
        var message = ModelCatalogStrings.Summary(result);
        return result.Outcome switch
        {
            ModelCatalogRefreshOutcome.Failed => AutomationRunOutcome.Failed(message),
            ModelCatalogRefreshOutcome.Skipped => AutomationRunOutcome.Skipped(message),
            _ => AutomationRunOutcome.Completed(message),
        };
    }
}
