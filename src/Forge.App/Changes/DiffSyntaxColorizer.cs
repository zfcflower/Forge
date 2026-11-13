using System.Diagnostics;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Forge.Workspace;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;
using FontStyle = Avalonia.Media.FontStyle;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Changes;

/// <summary>
/// Syntax colours for a multi-file diff document. The editor's own TextMate installation takes one grammar for the whole
/// document, so this transformer tokenizes each code row with the grammar of its file. Tokenization runs on the file's own
/// text (one stream per hunk and side, so a fold in the document never breaks a multi-line construct), lazily and only for
/// lines that are drawn.
/// </summary>
internal sealed class DiffSyntaxColorizer : DocumentColorizingTransformer
{
    /// <summary>Streams longer than this stay uncoloured, like the file editor's highlight limit.</summary>
    internal const int MaxStreamLines = 20_000;

    /// <summary>Furthest the tokenizer catches up from the last tokenized line before restarting from a clean state.</summary>
    internal const int MaxCatchUp = 4_000;

    private static readonly Dictionary<ThemeName, ThemeContext> Contexts = [];
    private readonly Dictionary<(int File, int Hunk, bool Old), Stream> streams = [];
    private readonly Dictionary<int, string?> scopes = [];
    private IReadOnlyList<FileDiff> diffs = [];
    private DiffRowMap map = DiffRowMap.Empty;
    private DiffGutterSide side;
    private ThemeName theme = ThemeName.LightPlus;
    private ThemeContext? context;

    public void Configure(DiffRowMap rows, IReadOnlyList<FileDiff> files, DiffGutterSide gutterSide, bool dark)
    {
        map = rows;
        diffs = files;
        side = gutterSide;
        theme = dark ? ThemeName.DarkPlus : ThemeName.LightPlus;
        context = null;
        streams.Clear();
        scopes.Clear();
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!map.Contains(line.LineNumber) || line.Length == 0)
        {
            return;
        }

        var row = map.ForLine(line.LineNumber);
        if (!row.IsCode || (uint)row.File >= (uint)diffs.Count)
        {
            return;
        }

        try
        {
            var tokens = TokensOf(row);
            if (tokens is null)
            {
                return;
            }

            var length = line.Length;
            foreach (var token in tokens)
            {
                var from = Math.Min(token.Start, length);
                var to = Math.Min(token.End, length);
                if (to <= from || token.Brush is null)
                {
                    continue;
                }

                var brush = token.Brush;
                var style = token.Style;
                ChangeLinePart(line.Offset + from, line.Offset + to, element =>
                {
                    element.TextRunProperties.SetForegroundBrush(brush);
                    if (style != FontStyle.Normal)
                    {
                        element.TextRunProperties.SetTypeface(new Typeface(element.TextRunProperties.Typeface.FontFamily, style));
                    }
                });
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            AppLogs.Diff.ZLogWarning($"syntax colouring failed: {exception.Message}");
        }
    }

    private Token[]? TokensOf(in DiffRow row)
    {
        var oldSide = side == DiffGutterSide.Old || (side == DiffGutterSide.Inline && row.Kind == DiffRowKind.Removed);
        var key = (row.File, row.Hunk, oldSide);
        if (!streams.TryGetValue(key, out var stream))
        {
            stream = CreateStream(row.File, row.Hunk, oldSide);
            streams[key] = stream;
        }

        return stream.TokensAt(row.Line);
    }

    private Stream CreateStream(int file, int hunkIndex, bool oldSide)
    {
        var diff = diffs[file];
        if ((uint)hunkIndex >= (uint)diff.Hunks.Count || ContextFor() is not { } ctx)
        {
            return new Stream(null, null, [], oldSide);
        }

        var extension = Path.GetExtension(diff.Path);
        if (extension.Length == 0)
        {
            return new Stream(null, ctx, [], oldSide);
        }

        if (!scopes.TryGetValue(file, out var scope))
        {
            scope = ctx.Options.GetScopeByExtension(extension);
            scopes[file] = scope;
        }

        var grammar = scope is null ? null : ctx.GrammarFor(scope);
        return new Stream(grammar, ctx, diff.Hunks[hunkIndex].Lines, oldSide);
    }

    private ThemeContext? ContextFor()
    {
        if (context is not null)
        {
            return context;
        }

        try
        {
            if (!Contexts.TryGetValue(theme, out var created))
            {
                created = new ThemeContext(theme);
                Contexts[theme] = created;
            }

            context = created;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or KeyNotFoundException
            or NotSupportedException or FormatException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            AppLogs.Diff.ZLogWarning($"syntax theme failed: {exception.Message}");
        }

        return context;
    }

    private readonly record struct Token(int Start, int End, IBrush? Brush, FontStyle Style);

    /// <summary>Registry, theme and grammars of one TextMate theme; shared by every colorizer.</summary>
    private sealed class ThemeContext
    {
        private readonly Registry registry;
        private readonly Dictionary<string, IGrammar?> grammars = [];
        private readonly Dictionary<int, IBrush?> brushes = [];

        public ThemeContext(ThemeName name)
        {
            Options = new RegistryOptions(name);
            registry = new Registry(Options);
            registry.SetTheme(Options.LoadTheme(name));
            Theme = registry.GetTheme();
        }

        public RegistryOptions Options { get; }

        public Theme Theme { get; }

        public IGrammar? GrammarFor(string scope)
        {
            if (!grammars.TryGetValue(scope, out var grammar))
            {
                try
                {
                    grammar = registry.LoadGrammar(scope);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException or KeyNotFoundException
                    or NotSupportedException or FormatException or System.Text.Json.JsonException)
                {
                    AppLogs.Diff.ZLogWarning($"grammar failed: {exception.Message}");
                    grammar = null;
                }

                grammars[scope] = grammar;
            }

            return grammar;
        }

        public IBrush? BrushOf(int colorId)
        {
            if (colorId <= 0)
            {
                return null;
            }

            if (!brushes.TryGetValue(colorId, out var brush))
            {
                var text = Theme.GetColor(colorId);
                brush = !string.IsNullOrEmpty(text) && Color.TryParse(text, out var color) ? new SolidColorBrush(color) : null;
                brushes[colorId] = brush;
            }

            return brush;
        }
    }

    /// <summary>The tokens of one side of one hunk, computed in order and kept.</summary>
    private sealed class Stream
    {
        private readonly IGrammar? grammar;
        private readonly ThemeContext? theme;
        private readonly int[] positionOfLine;
        private readonly int[] lineOfPosition;
        private readonly IReadOnlyList<DiffLine> lines;
        private readonly Token[]?[] tokens;
        private IStateStack? state;
        private int done;

        public Stream(IGrammar? grammar, ThemeContext? theme, IReadOnlyList<DiffLine> lines, bool oldSide)
        {
            this.grammar = grammar;
            this.theme = theme;
            this.lines = lines;
            positionOfLine = new int[lines.Count];
            var count = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                var belongs = oldSide ? lines[i].Kind != DiffLineKind.Added : lines[i].Kind != DiffLineKind.Removed;
                positionOfLine[i] = belongs ? count++ : -1;
            }

            lineOfPosition = new int[count];
            for (var i = 0; i < lines.Count; i++)
            {
                if (positionOfLine[i] >= 0)
                {
                    lineOfPosition[positionOfLine[i]] = i;
                }
            }

            tokens = grammar is null || count > MaxStreamLines ? [] : new Token[]?[count];
        }

        public Token[]? TokensAt(int lineIndex)
        {
            if (tokens.Length == 0 || (uint)lineIndex >= (uint)positionOfLine.Length)
            {
                return null;
            }

            var position = positionOfLine[lineIndex];
            if (position < 0)
            {
                return null;
            }

            if (position - done > MaxCatchUp)
            {
                done = position - 200;
                state = null;
            }

            while (done <= position)
            {
                tokens[done] = Tokenize(lines[lineOfPosition[done]].Text);
                done++;
            }

            return tokens[position];
        }

        private Token[]? Tokenize(string text)
        {
            var clean = text.Replace("\r", string.Empty);
            var result = grammar!.TokenizeLine(clean, state, TimeSpan.FromMilliseconds(20));
            state = result.RuleStack;
            var list = new List<Token>(result.Tokens.Length);
            foreach (var token in result.Tokens)
            {
                int foreground = -1, style = -1;
                foreach (var rule in theme!.Theme.Match(token.Scopes))
                {
                    if (foreground < 0 && rule.foreground > 0)
                    {
                        foreground = rule.foreground;
                    }

                    if (style < 0 && rule.fontStyle > 0)
                    {
                        style = (int)rule.fontStyle;
                    }
                }

                var fontStyle = style > 0 && (style & (int)TextMateSharp.Themes.FontStyle.Italic) != 0 ? FontStyle.Italic : FontStyle.Normal;
                var brush = theme.BrushOf(foreground);
                if (brush is not null || fontStyle != FontStyle.Normal)
                {
                    list.Add(new Token(token.StartIndex, token.EndIndex, brush, fontStyle));
                }
            }

            return [.. list];
        }
    }
}
