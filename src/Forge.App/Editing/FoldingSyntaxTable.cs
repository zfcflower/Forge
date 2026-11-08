using Forge.Editor.Folding;

namespace Forge.App.Editing;

/// <summary>How one file folds locally. The extension table lives here, in the UI layer, because it is a presentation
/// choice; <see cref="Forge.Editor.Folding.StructuralFolding"/> only knows the shapes.</summary>
internal static class FoldingSyntaxTable
{
    private static readonly HashSet<string> Xml = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".xaml", ".axaml", ".svg", ".html", ".htm", ".xhtml", ".csproj", ".vbproj", ".fsproj", ".props", ".targets",
        ".resx", ".xsd", ".xsl", ".xslt", ".plist", ".storyboard", ".xib", ".nuspec", ".rss", ".atom", ".wsdl", ".config",
        ".manifest", ".dtd", ".rdf",
    };

    private static readonly HashSet<string> Indent = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyi", ".pyw", ".yaml", ".yml", ".fs", ".fsi", ".fsx", ".nim", ".nims", ".coffee", ".haml", ".sass", ".styl",
        ".gd", ".hcl", ".tf", ".tfvars", ".nix", ".rego",
    };

    private static readonly HashSet<string> Braces = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".java", ".js", ".mjs", ".cjs", ".jsx", ".ts", ".mts", ".cts", ".tsx", ".json", ".jsonc", ".json5",
        ".css", ".scss", ".less", ".rs", ".go", ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh", ".hxx", ".m", ".mm",
        ".swift", ".kt", ".kts", ".php", ".phtml", ".sql", ".proto", ".gradle", ".groovy", ".dart", ".scala", ".sc", ".ps1",
        ".psm1", ".psd1", ".zig", ".v", ".sv", ".sol", ".glsl", ".vert", ".frag", ".hlsl", ".shader", ".razor", ".cshtml",
        ".vue", ".svelte", ".toml", ".ini", ".cfg", ".conf",
    };

    /// <summary>The heuristic a file folds with; an unknown extension folds only on explicit region markers.</summary>
    public static FoldingShape ForPath(string? path)
    {
        var extension = string.IsNullOrEmpty(path) ? string.Empty : System.IO.Path.GetExtension(path);
        if (extension.Length == 0)
        {
            return new FoldingShape(FoldingSyntax.Region, Xml: false);
        }

        if (Xml.Contains(extension))
        {
            return new FoldingShape(FoldingSyntax.Region, Xml: true);
        }

        if (Indent.Contains(extension))
        {
            return new FoldingShape(FoldingSyntax.Indent, Xml: false);
        }

        return Braces.Contains(extension)
            ? new FoldingShape(FoldingSyntax.Braces, Xml: false)
            : new FoldingShape(FoldingSyntax.Region, Xml: false);
    }

    /// <summary>Whether the file is C#-family; those get the language's indentation strategy.</summary>
    public static bool IsCSharp(string? path) =>
        path is not null && System.IO.Path.GetExtension(path) is ".cs" or ".csx";
}

/// <summary>Which local folding heuristic a file uses, and whether the library's XML strategy handles it.</summary>
internal readonly record struct FoldingShape(FoldingSyntax Syntax, bool Xml);
