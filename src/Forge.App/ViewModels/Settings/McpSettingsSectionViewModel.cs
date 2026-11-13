using Forge.App.Composition;
using Forge.App.Localization;

namespace Forge.App.ViewModels.Settings;

/// <summary>The <c>mcp</c> section: the servers in <c>mcp.json</c> and what the manager reports for each.</summary>
internal sealed class McpSettingsSectionViewModel(AppComposition composition) : InstrumentSettingsSectionViewModel
{
    protected override string HintText => InstrumentsStrings.McpSectionHint;

    public override void Reload()
    {
        var states = composition.Mcp?.States ?? [];
        Replace(
            states.Select(static state => new InstrumentRow(
                state.Name,
                state.Enabled ? InstrumentsStrings.McpSectionEnabled : InstrumentsStrings.McpSectionDisabled,
                state.LastError is { Length: > 0 } error
                    ? state.Status.ToString() + " · " + Shorten(error)
                    : state.Status.ToString())),
            InstrumentsStrings.McpSectionEmpty);
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..79] + "…";
}
