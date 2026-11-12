using System.Collections.ObjectModel;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.ViewModels;

namespace Forge.App.Settings.Prompts;

/// <summary>One row of the template list: kind icon, name, a source badge and the extra badges of one <see cref="PromptTemplateEntry"/>.</summary>
internal sealed class PromptRowViewModel : ObservableObject
{
    private readonly PromptsSettingsViewModel owner;
    private bool isSelected;

    public PromptRowViewModel(PromptsSettingsViewModel owner, PromptTemplateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(entry);
        this.owner = owner;
        Entry = entry;
        SelectCommand = new AsyncRelayCommand(_ => owner.SelectAsync(this));
    }

    public PromptTemplateEntry Entry { get; }

    public PromptTemplateRef Ref => Entry.Ref;

    public string Id => Entry.Ref.Id;

    public PromptTemplateKind Kind => Entry.Ref.Kind;

    public PromptTemplateScope Scope => Entry.Ref.Scope;

    /// <summary>Built-in names come from Core in English and are shown as written.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Entry.DisplayName) ? Entry.Ref.Id : Entry.DisplayName;

    public string Description => Entry.Description;

    public string? Path => Entry.Path;

    public bool IsCommandKind => Kind == PromptTemplateKind.Command;

    public bool IsAgentKind => Kind == PromptTemplateKind.Agent;

    public bool IsSlotKind => Kind == PromptTemplateKind.Slot;

    public bool IsBuiltIn => Scope == PromptTemplateScope.BuiltIn;

    public bool IsGlobal => Scope == PromptTemplateScope.Global;

    public bool IsProject => Scope == PromptTemplateScope.Project;

    public string ScopeText => ScopeName(Scope);

    /// <summary>A custom entry that has a built-in of the same kind and id: saving it replaces (or extends) that built-in.</summary>
    public bool Overrides => !IsBuiltIn && Entry.HasBuiltIn;

    public string OverridesText => Loc.Instance[PromptsStrings.BadgeOverridesKey];

    public bool IsAppend => !IsBuiltIn && Entry.Mode == PromptTemplateMode.Append;

    public string AppendText => Loc.Instance[PromptsStrings.BadgeAppendKey];

    public bool IsShadowed => Entry.IsShadowed;

    public string ShadowedText => Loc.Instance[PromptsStrings.BadgeShadowedKey];

    /// <summary>A shadowed row is dimmed; it is still listed so it can be opened and edited.</summary>
    public double RowOpacity => IsShadowed ? 0.55 : 1.0;

    public bool IsInvalid => !Entry.IsValid;

    public string InvalidText => Loc.Instance[PromptsStrings.BadgeInvalidKey];

    /// <summary>The problems of an invalid file, one per line, for the badge tooltip.</summary>
    public string ProblemsText => string.Join('\n', Entry.Problems.Select(PromptsStrings.Problem));

    /// <summary>Hover text of the row: the file path, or the description for a built-in (which has no file).</summary>
    public string ToolTipText => Entry.Path ?? Entry.Description;

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    public AsyncRelayCommand SelectCommand { get; }

    internal static string ScopeName(PromptTemplateScope scope) => Loc.Instance[scope switch
    {
        PromptTemplateScope.BuiltIn => PromptsStrings.ScopeBuiltInKey,
        PromptTemplateScope.Global => PromptsStrings.ScopeGlobalKey,
        _ => PromptsStrings.ScopeProjectKey,
    }];

    internal static string KindName(PromptTemplateKind kind) => Loc.Instance[kind switch
    {
        PromptTemplateKind.Command => PromptsStrings.KindCommandKey,
        PromptTemplateKind.Agent => PromptsStrings.KindAgentKey,
        _ => PromptsStrings.KindSlotKey,
    }];
}

/// <summary>One heading of the list (commands, agents, built-in prompts) and its rows.</summary>
internal sealed class PromptGroupViewModel
{
    public PromptGroupViewModel(PromptTemplateKind kind, string title, IEnumerable<PromptRowViewModel> rows)
    {
        Kind = kind;
        Title = title;
        Rows = new ObservableCollection<PromptRowViewModel>(rows);
    }

    public PromptTemplateKind Kind { get; }

    public string Title { get; }

    public ObservableCollection<PromptRowViewModel> Rows { get; }
}
