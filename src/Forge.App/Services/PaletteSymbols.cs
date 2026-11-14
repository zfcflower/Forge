using Forge.Editor.Lsp;

namespace Forge.App.Services;

/// <summary>
/// The language server's symbol tree as the palette's flat list: depth first, each symbol with the name of the symbol it
/// sits in. The jump target is the selection range's start, which is the identifier itself rather than the whole body of
/// a method, so revealing it puts the caret on the name.
/// </summary>
internal static class PaletteSymbols
{
    /// <summary>
    /// Symbols collected from one document at most. A generated file can carry tens of thousands; the palette ranks what it
    /// has, and past this bound the extra symbols could not reach the list anyway.
    /// </summary>
    public const int MaxSymbols = 2000;

    /// <summary>Flattens a document's symbols; null or empty input yields an empty list.</summary>
    public static IReadOnlyList<PaletteSymbol> Flatten(IReadOnlyList<DocumentSymbol>? symbols)
    {
        if (symbols is null || symbols.Count == 0)
        {
            return [];
        }

        var flattened = new List<PaletteSymbol>(Math.Min(symbols.Count * 4, MaxSymbols));
        foreach (var symbol in symbols)
        {
            Walk(symbol, string.Empty, flattened);
            if (flattened.Count >= MaxSymbols)
            {
                break;
            }
        }

        return flattened;
    }

    private static void Walk(DocumentSymbol symbol, string container, List<PaletteSymbol> flattened)
    {
        if (flattened.Count >= MaxSymbols)
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(symbol.Name) ? "?" : symbol.Name;
        flattened.Add(new PaletteSymbol(
            name,
            container,
            symbol.Kind,
            symbol.SelectionRange.Start.Line,
            symbol.SelectionRange.Start.Character));

        if (symbol.Children.Count == 0)
        {
            return;
        }

        foreach (var child in symbol.Children)
        {
            Walk(child, name, flattened);
            if (flattened.Count >= MaxSymbols)
            {
                return;
            }
        }
    }
}
