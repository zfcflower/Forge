using Forge.App.Localization;
using Forge.Editor.Lsp;
using Forge.IO;
using ZLinq;

namespace Forge.App.Services;

/// <summary>
/// Turns one source's snapshot into rows: matching, ranking and taking the configured limit. The functions are pure
/// (no window, no file system, no language server) so the ranking each source uses is pinned by unit tests, and each one
/// runs a single pass over its snapshot with pooled LINQ buffers - a keystroke over a 20 000-entry file list allocates the
/// ranked pairs and the result rows, nothing else.
/// </summary>
internal static class PaletteSources
{
    /// <summary>
    /// Files of the workspace, best name match first. The matcher is <see cref="ComposerSuggestions.FilterFiles"/>, the
    /// same one the composer's <c>@</c> picker uses, so a file searched from either place ranks the same way.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Files(IReadOnlyList<string> files, string query, int limit)
    {
        var matches = ComposerSuggestions.FilterFiles(files, query, limit);
        if (matches.Count == 0)
        {
            return [];
        }

        var items = new PaletteItem[matches.Count];
        for (var index = 0; index < matches.Count; index++)
        {
            var path = matches[index];
            var separator = path.LastIndexOf('/');
            items[index] = new PaletteItem
            {
                Source = PaletteSource.Files,
                Kind = PaletteItemKind.File,
                Title = separator < 0 ? path : path[(separator + 1)..],
                Detail = separator < 0 ? string.Empty : path[..separator],
                Path = path,
            };
        }

        return items;
    }

    /// <summary>
    /// Matching lines of a content search, sorted by path and line so the same result always reads the same way (the
    /// engine reports lines in completion order). A line under an excluded directory is dropped, and the list stops at
    /// <paramref name="limit"/>.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Content(
        GrepResult result,
        IReadOnlyList<string> excludedDirectories,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(excludedDirectories);
        if (limit <= 0 || result.Lines.Count == 0)
        {
            return [];
        }

        var kept = new List<GrepLine>(Math.Min(result.Lines.Count, limit));
        foreach (var line in result.Lines)
        {
            if (!line.IsMatch || IsExcluded(line.Path, excludedDirectories))
            {
                continue;
            }

            kept.Add(line);
        }

        kept.Sort(static (left, right) =>
        {
            var byPath = string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
            return byPath != 0 ? byPath : left.LineNumber.CompareTo(right.LineNumber);
        });

        var count = Math.Min(kept.Count, limit);
        var items = new PaletteItem[count];
        for (var index = 0; index < count; index++)
        {
            var line = kept[index];
            items[index] = new PaletteItem
            {
                Source = PaletteSource.Content,
                Kind = PaletteItemKind.ContentLine,
                Title = line.Path + ':' + line.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Detail = line.Text.Trim(),
                Path = line.Path,
                Line = line.LineNumber,
            };
        }

        return items;
    }

    /// <summary>
    /// Root sessions, best title match first: the title ranks as the primary text, the project and branch as the
    /// secondary one, so a query that only names the project still finds the sessions of that project. Sub-agent sessions
    /// are left out - the palette opens root sessions, which is what the tree does too - and an archived one is badged.
    /// </summary>
    public static IReadOnlyList<PaletteItem> Sessions(
        IReadOnlyList<PaletteSessionSnapshot> sessions,
        string query,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (limit <= 0 || sessions.Count == 0)
        {
            return [];
        }

        var ranked = sessions
            .AsValueEnumerable()
            .Where(static session => !session.IsSubAgent)
            .Select(session => (Session: session, Rank: PaletteQuery.ScorePair(session.Title, session.Project + ' ' + session.Branch, query)))
            .Where(pair => pair.Rank >= 0)
            .OrderBy(pair => pair.Rank)
            .ThenByDescending(pair => pair.Session.LastActiveAt)
            .Take(limit)
            .ToList();

        var items = new PaletteItem[ranked.Count];
        for (var index = 0; index < ranked.Count; index++)
        {
            var session = ranked[index].Session;
            items[index] = new PaletteItem
            {
                Source = PaletteSource.Sessions,
                Kind = PaletteItemKind.Session,
                Title = session.Title,
                Detail = string.IsNullOrEmpty(session.Project) ? session.Branch : session.Project + " · " + session.Branch,
                Badge = session.IsArchived ? Loc.Instance[PaletteStrings.SessionArchived] : string.Empty,
                SessionId = session.Id,
            };
        }

        return items;
    }

    /// <summary>Commands, best title match first, with the category as the secondary text.</summary>
    public static IReadOnlyList<PaletteItem> Commands(IReadOnlyList<PaletteCommand> commands, string query, int limit)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (limit <= 0 || commands.Count == 0)
        {
            return [];
        }

        var ranked = commands
            .AsValueEnumerable()
            .Select(command => (Command: command, Rank: PaletteQuery.ScorePair(command.Title, command.Category, query)))
            .Where(pair => pair.Rank >= 0)
            .OrderBy(pair => pair.Rank)
            .ThenBy(pair => pair.Command.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();

        var items = new PaletteItem[ranked.Count];
        for (var index = 0; index < ranked.Count; index++)
        {
            var command = ranked[index].Command;
            items[index] = new PaletteItem
            {
                Source = PaletteSource.Commands,
                Kind = PaletteItemKind.Command,
                Title = command.Title,
                Detail = command.Category,
                Badge = command.Gesture,
                CommandId = command.Id,
                Gesture = command.Gesture,
            };
        }

        return items;
    }

    /// <summary>Symbols of the active document, best name match first; the container is the secondary text.</summary>
    public static IReadOnlyList<PaletteItem> Symbols(PaletteSymbolSnapshot snapshot, string query, int limit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (limit <= 0 || snapshot.Symbols.Count == 0)
        {
            return [];
        }

        var ranked = snapshot.Symbols
            .AsValueEnumerable()
            .Select(symbol => (Symbol: symbol, Rank: PaletteQuery.ScorePair(symbol.Name, symbol.Container, query)))
            .Where(pair => pair.Rank >= 0)
            .OrderBy(pair => pair.Rank)
            .ThenBy(pair => pair.Symbol.Line)
            .Take(limit)
            .ToList();

        var items = new PaletteItem[ranked.Count];
        for (var index = 0; index < ranked.Count; index++)
        {
            var symbol = ranked[index].Symbol;
            items[index] = new PaletteItem
            {
                Source = PaletteSource.Symbols,
                Kind = PaletteItemKind.Symbol,
                Title = symbol.Name,
                Detail = symbol.Container,
                Badge = SymbolKindBadge(symbol.Kind),
                Path = snapshot.Document,
                Line = symbol.Line + 1,
                Column = symbol.Column + 1,
            };
        }

        return items;
    }

    /// <summary>
    /// True when one of the path's directory segments is in <paramref name="excludedDirectories"/>. The scan is
    /// allocation-free (a segment is compared in place), and it deliberately matches a name at any depth: an exclusion is
    /// a directory name, not a path, so <c>node_modules</c> means every one of them.
    /// </summary>
    public static bool IsExcluded(string? path, IReadOnlyList<string> excludedDirectories)
    {
        if (string.IsNullOrEmpty(path) || excludedDirectories.Count == 0)
        {
            return false;
        }

        for (var start = 0; start <= path.Length; start++)
        {
            var end = path.IndexOf('/', start);
            if (end < 0)
            {
                end = path.Length;
            }

            if (end > start)
            {
                var segment = path.AsSpan(start, end - start);
                foreach (var excluded in excludedDirectories)
                {
                    if (segment.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            if (end == path.Length)
            {
                return false;
            }

            start = end;
        }

        return false;
    }

    /// <summary>The badge word of a symbol kind, at the granularity the list needs.</summary>
    public static string SymbolKindBadge(SymbolKind kind) => kind switch
    {
        SymbolKind.Class or SymbolKind.Interface or SymbolKind.Enum or SymbolKind.Struct or SymbolKind.TypeParameter or
            SymbolKind.Namespace or SymbolKind.Module or SymbolKind.Package or SymbolKind.File => Loc.Instance[PaletteStrings.SymbolKindType],
        SymbolKind.Method or SymbolKind.Function or SymbolKind.Constructor or SymbolKind.Operator or SymbolKind.Event =>
            Loc.Instance[PaletteStrings.SymbolKindFunction],
        SymbolKind.Property or SymbolKind.Field or SymbolKind.EnumMember or SymbolKind.Constant =>
            Loc.Instance[PaletteStrings.SymbolKindMember],
        SymbolKind.Variable or SymbolKind.Key => Loc.Instance[PaletteStrings.SymbolKindVariable],
        _ => Loc.Instance[PaletteStrings.SymbolKindOther],
    };
}
