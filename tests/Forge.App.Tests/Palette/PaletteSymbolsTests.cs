using Forge.App.Services;
using Forge.Editor.Lsp;
using Xunit;

namespace Forge.App.Tests.Palette;

/// <summary>
/// Pins the flattened form of a document's symbol tree: depth first, each symbol carrying the name of the symbol it sits
/// in (empty at the root), the position taken from the selection range rather than the whole body, and a hard cap at
/// <see cref="PaletteSymbols.MaxSymbols"/> so a generated file cannot flood the list.
/// </summary>
public sealed class PaletteSymbolsTests
{
    [Fact]
    public void Flatten_walks_depth_first_and_names_the_parent_as_the_container()
    {
        DocumentSymbol[] tree =
        [
            Symbol(
                "App",
                SymbolKind.Class,
                0,
                0,
                Symbol("Run", SymbolKind.Method, 3, 4, Symbol("state", SymbolKind.Field, 5, 8)),
                Symbol("Helper", SymbolKind.Class, 20, 4)),
        ];

        var flat = PaletteSymbols.Flatten(tree);

        Assert.Equal(["App", "Run", "state", "Helper"], flat.Select(static symbol => symbol.Name).ToArray());
        Assert.Equal([string.Empty, "App", "Run", "App"], flat.Select(static symbol => symbol.Container).ToArray());
        Assert.Equal(
            [SymbolKind.Class, SymbolKind.Method, SymbolKind.Field, SymbolKind.Class],
            flat.Select(static symbol => symbol.Kind).ToArray());
    }

    [Fact]
    public void Flatten_takes_the_position_from_the_selection_range()
    {
        // Selection range 3:4 is the identifier; the body range spans the whole method and must not be used.
        DocumentSymbol[] tree =
        [
            Symbol("Run", SymbolKind.Method, selectionLine: 3, selectionCharacter: 4),
            Symbol("Stop", SymbolKind.Method, selectionLine: 12, selectionCharacter: 7),
        ];

        var flat = PaletteSymbols.Flatten(tree);

        Assert.Equal([3, 12], flat.Select(static symbol => symbol.Line).ToArray());
        Assert.Equal([4, 7], flat.Select(static symbol => symbol.Column).ToArray());
    }

    [Fact]
    public void Flatten_of_null_or_empty_input_is_empty()
    {
        Assert.Empty(PaletteSymbols.Flatten(null));
        Assert.Empty(PaletteSymbols.Flatten([]));
    }

    [Fact]
    public void Flatten_stops_at_the_symbol_cap()
    {
        var children = new DocumentSymbol[2500];
        for (var index = 0; index < children.Length; index++)
        {
            children[index] = Symbol("child" + index, SymbolKind.Method, index + 1, 0);
        }

        var flat = PaletteSymbols.Flatten([Symbol("Root", SymbolKind.Class, 0, 0, children)]);

        Assert.Equal(2000, PaletteSymbols.MaxSymbols);
        Assert.Equal(PaletteSymbols.MaxSymbols, flat.Count);
        Assert.Equal("Root", flat[0].Name);
        Assert.Equal("child1998", flat[^1].Name);
    }

    /// <summary>One symbol whose body spans far past its identifier, so only the selection range can supply a position.</summary>
    private static DocumentSymbol Symbol(
        string name,
        SymbolKind kind,
        int selectionLine,
        int selectionCharacter,
        params DocumentSymbol[] children) =>
        new(
            name,
            null,
            kind,
            new LspRange(new LspPosition(selectionLine, 0), new LspPosition(selectionLine + 40, 0)),
            new LspRange(
                new LspPosition(selectionLine, selectionCharacter),
                new LspPosition(selectionLine, selectionCharacter + name.Length)),
            children);
}
