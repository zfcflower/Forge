using Forge.App.Editing;
using Forge.App.Localization;

namespace Forge.App.ViewModels;

/// <summary>
/// The rows of the "find references" list: one per place the server reports, in reading order, with the location already
/// formatted in the language of the moment the list was built. The list is rebuilt on every request rather than updated,
/// so it holds no state that a language switch could leave behind.
/// </summary>
internal sealed class ReferenceListViewModel
{
    private readonly Action<EditorLspReference> open;

    public ReferenceListViewModel(EditorLspReferenceList list, string symbolName, Action<EditorLspReference> open)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(open);
        this.open = open;
        SymbolName = symbolName;
        IsTruncated = list.Truncated;
        Items = [.. list.Items.Select(static reference => new ReferenceRowViewModel(
            InspectorStrings.EditorLspReferenceLocation(reference.Line, reference.Column),
            System.IO.Path.GetFileName(reference.Path),
            System.IO.Path.GetDirectoryName(reference.Path) ?? string.Empty,
            reference))];
    }

    /// <summary>The identifier the request was made for, or empty when the caret was not on one.</summary>
    public string SymbolName { get; }

    public IReadOnlyList<ReferenceRowViewModel> Items { get; }

    /// <summary>True when the row cap cut the list, so a capped list never reads as every use of the symbol.</summary>
    public bool IsTruncated { get; }

    /// <summary>"3 处引用" / "3 references", or the symbol it belongs to when one was read.</summary>
    public string Summary => (SymbolName.Length == 0
        ? InspectorStrings.EditorLspReferences(Items.Count).Resolve()
        : SymbolName + " · " + InspectorStrings.EditorLspReferences(Items.Count).Resolve())
        + (IsTruncated ? " · " + InspectorStrings.ProblemsTruncatedRows(Items.Count).Resolve() : string.Empty);

    /// <summary>The keyboard hint under the list; the flyout closes on Escape by itself.</summary>
    public static string Hint => Loc.Instance[InspectorStrings.EditorLspReferencesHint];

    public void Open(ReferenceRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        open(row.Reference);
    }
}

/// <summary>One reference: where it is, which file it is in, and everything navigation needs.</summary>
internal sealed record ReferenceRowViewModel(string Location, string FileName, string Directory, EditorLspReference Reference)
{
    /// <summary>"L42:9 — src/Forge.App/Foo.cs" for the row's tooltip and accessible name.</summary>
    public string Description => Directory.Length == 0 ? FileName : FileName + " — " + Directory;
}
