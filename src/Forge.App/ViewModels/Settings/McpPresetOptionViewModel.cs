using System.Collections.ObjectModel;
using System.Windows.Input;
using ICommand = System.Windows.Input.ICommand;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.Mcp;

namespace Forge.App.ViewModels.Settings;

/// <summary>One built-in preset: its localized title and note, and the apply command that opens the editor prefilled.</summary>
internal sealed class McpPresetOptionViewModel
{
    public McpPresetOptionViewModel(McpPreset preset, ICommand applyCommand)
    {
        Preset = preset ?? throw new ArgumentNullException(nameof(preset));
        ApplyCommand = applyCommand ?? throw new ArgumentNullException(nameof(applyCommand));
    }

    public McpPreset Preset { get; }

    public ICommand ApplyCommand { get; }

    public string Title => McpSettingsStrings.PresetTitle(Preset.Id);

    public string Description => McpSettingsStrings.PresetDescription(Preset.Id);

    public bool NeedsPath => Preset.NeedsPath;
}
