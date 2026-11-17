using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Forge.App.Changes.Markdown;

/// <summary>
/// A small TeX math subset: parse (<see cref="MathParser"/>) into nodes, lay the nodes out as boxes with a width, an ascent and
/// a descent (<see cref="MathBox"/>) and draw them with <see cref="FormattedText"/>. Covers what chat replies use: letters and
/// numbers, <c>^ _</c>, <c>\frac</c>, <c>\sqrt</c>, big operators with limits, Greek letters and the common symbols, accents-free
/// text (<c>\text</c>, <c>\mathrm</c>, <c>\mathbf</c>), <c>\left…\right</c> delimiters and simple matrices / cases. Anything it does
/// not understand makes the parse fail, and the caller shows the TeX source instead - never a half-drawn formula.
/// </summary>
internal abstract class MathNode;

internal sealed class MathRow(List<MathNode> items) : MathNode
{
    public List<MathNode> Items { get; } = items;
}

internal sealed class MathText(string text, bool italic, bool bold = false) : MathNode
{
    public string Text { get; } = text;

    public bool Italic { get; } = italic;

    public bool Bold { get; } = bold;
}

internal sealed class MathScripts(MathNode baseNode, MathNode? sup, MathNode? sub, bool limits) : MathNode
{
    public MathNode Base { get; } = baseNode;

    public MathNode? Sup { get; } = sup;

    public MathNode? Sub { get; } = sub;

    /// <summary>Limits go above and below the base (in display style) instead of beside it.</summary>
    public bool Limits { get; } = limits;
}

internal sealed class MathFraction(MathNode numerator, MathNode denominator) : MathNode
{
    public MathNode Numerator { get; } = numerator;

    public MathNode Denominator { get; } = denominator;
}

internal sealed class MathRoot(MathNode body, MathNode? index) : MathNode
{
    public MathNode Body { get; } = body;

    public MathNode? Index { get; } = index;
}

internal sealed class MathSpace(double ems) : MathNode
{
    public double Ems { get; } = ems;
}

internal sealed class MathFenced(string open, MathNode body, string close) : MathNode
{
    public string Open { get; } = open;

    public MathNode Body { get; } = body;

    public string Close { get; } = close;
}

internal sealed class MathMatrix(List<List<MathNode>> rows, string open, string close, bool cases) : MathNode
{
    public List<List<MathNode>> Rows { get; } = rows;

    public string Open { get; } = open;

    public string Close { get; } = close;

    public bool Cases { get; } = cases;
}

internal static class MathParser
{
    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ϵ", ["varepsilon"] = "ε", ["zeta"] = "ζ",
        ["eta"] = "η", ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν",
        ["xi"] = "ξ", ["pi"] = "π", ["varpi"] = "ϖ", ["rho"] = "ρ", ["varrho"] = "ϱ", ["sigma"] = "σ", ["varsigma"] = "ς", ["tau"] = "τ",
        ["upsilon"] = "υ", ["phi"] = "ϕ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ",
        ["Upsilon"] = "Υ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["pm"] = "±", ["mp"] = "∓", ["times"] = "×", ["div"] = "÷", ["cdot"] = "·", ["ast"] = "∗", ["star"] = "⋆", ["circ"] = "∘",
        ["bullet"] = "•", ["oplus"] = "⊕", ["otimes"] = "⊗", ["cap"] = "∩", ["cup"] = "∪", ["wedge"] = "∧", ["land"] = "∧",
        ["vee"] = "∨", ["lor"] = "∨", ["neg"] = "¬", ["lnot"] = "¬", ["setminus"] = "∖",
        ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥", ["geq"] = "≥", ["ne"] = "≠", ["neq"] = "≠", ["approx"] = "≈", ["equiv"] = "≡",
        ["sim"] = "∼", ["simeq"] = "≃", ["cong"] = "≅", ["propto"] = "∝", ["ll"] = "≪", ["gg"] = "≫", ["in"] = "∈", ["notin"] = "∉",
        ["ni"] = "∋", ["subset"] = "⊂", ["supset"] = "⊃", ["subseteq"] = "⊆", ["supseteq"] = "⊇", ["perp"] = "⊥", ["parallel"] = "∥",
        ["mid"] = "∣", ["to"] = "→", ["rightarrow"] = "→", ["leftarrow"] = "←", ["gets"] = "←", ["leftrightarrow"] = "↔",
        ["Rightarrow"] = "⇒", ["Leftarrow"] = "⇐", ["Leftrightarrow"] = "⇔", ["implies"] = "⟹", ["iff"] = "⟺", ["mapsto"] = "↦",
        ["uparrow"] = "↑", ["downarrow"] = "↓", ["infty"] = "∞", ["partial"] = "∂", ["nabla"] = "∇", ["forall"] = "∀",
        ["exists"] = "∃", ["nexists"] = "∄", ["emptyset"] = "∅", ["varnothing"] = "∅", ["hbar"] = "ℏ", ["ell"] = "ℓ", ["Re"] = "ℜ",
        ["Im"] = "ℑ", ["aleph"] = "ℵ", ["angle"] = "∠", ["triangle"] = "△", ["therefore"] = "∴", ["because"] = "∵",
        ["ldots"] = "…", ["dots"] = "…", ["cdots"] = "⋯", ["vdots"] = "⋮", ["ddots"] = "⋱", ["prime"] = "′", ["degree"] = "°",
        ["langle"] = "⟨", ["rangle"] = "⟩", ["lceil"] = "⌈", ["rceil"] = "⌉", ["lfloor"] = "⌊", ["rfloor"] = "⌋",
        ["lbrace"] = "{", ["rbrace"] = "}", ["lbrack"] = "[", ["rbrack"] = "]", ["vert"] = "|", ["Vert"] = "‖", ["backslash"] = "\\",
    };

    private static readonly HashSet<string> BigOperators = new(StringComparer.Ordinal)
    {
        "sum", "prod", "coprod", "int", "iint", "iiint", "oint", "bigcup", "bigcap", "bigoplus", "bigotimes", "bigvee", "bigwedge",
    };

    private static readonly Dictionary<string, string> BigGlyphs = new(StringComparer.Ordinal)
    {
        ["sum"] = "∑", ["prod"] = "∏", ["coprod"] = "∐", ["int"] = "∫", ["iint"] = "∬", ["iiint"] = "∭", ["oint"] = "∮",
        ["bigcup"] = "⋃", ["bigcap"] = "⋂", ["bigoplus"] = "⨁", ["bigotimes"] = "⨂", ["bigvee"] = "⋁", ["bigwedge"] = "⋀",
    };

    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "sin", "cos", "tan", "cot", "sec", "csc", "arcsin", "arccos", "arctan", "sinh", "cosh", "tanh", "log", "ln", "lg", "exp",
        "lim", "limsup", "liminf", "max", "min", "sup", "inf", "det", "dim", "gcd", "deg", "arg", "ker", "Pr", "mod", "sgn",
    };

    private static readonly HashSet<string> LimitFunctions = new(StringComparer.Ordinal) { "lim", "limsup", "liminf", "max", "min", "sup", "inf" };

    private static readonly Dictionary<char, string> Blackboard = new()
    {
        ['R'] = "ℝ", ['N'] = "ℕ", ['Z'] = "ℤ", ['Q'] = "ℚ", ['C'] = "ℂ", ['P'] = "ℙ", ['H'] = "ℍ",
    };

    /// <summary>Parses <paramref name="tex"/>; null when it uses something outside the supported subset.</summary>
    internal static MathNode? TryParse(string tex)
    {
        if (tex.Length > 4000)
        {
            return null;
        }

        var parser = new Reader(tex);
        try
        {
            var row = parser.ParseRow(closer: '\0');
            return parser.AtEnd ? row : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class Reader(string source)
    {
        private int position;
        private int depth;

        internal bool AtEnd => position >= source.Length;

        internal MathNode ParseRow(char closer)
        {
            if (++depth > 40)
            {
                throw new FormatException("nesting");
            }

            var items = new List<MathNode>();
            while (position < source.Length)
            {
                var c = source[position];
                if (c == closer && closer != '\0')
                {
                    break;
                }

                if (c == '}' || c == '&' || (c == '\\' && Peek("\\\\")) || (c == '\\' && (Peek("\\right") || Peek("\\end"))))
                {
                    break;
                }

                if (char.IsWhiteSpace(c))
                {
                    position++;
                    continue;
                }

                items.Add(ParseScripts(ParseAtom()));
            }

            depth--;
            return items.Count == 1 ? items[0] : new MathRow(items);
        }

        private bool Peek(string text) => string.CompareOrdinal(source, position, text, 0, text.Length) == 0 &&
            (text[^1] != 'd' && text[^1] != 't' || position + text.Length >= source.Length || !char.IsLetter(source[position + text.Length]));

        private MathNode ParseScripts(MathNode baseNode)
        {
            MathNode? sup = null;
            MathNode? sub = null;
            while (true)
            {
                SkipSpaces();
                if (position >= source.Length)
                {
                    break;
                }

                if (source[position] == '^' && sup is null)
                {
                    position++;
                    sup = ParseArgument();
                }
                else if (source[position] == '_' && sub is null)
                {
                    position++;
                    sub = ParseArgument();
                }
                else if (source[position] == '\'')
                {
                    position++;
                    sup ??= new MathText("′", italic: false);
                }
                else
                {
                    break;
                }
            }

            if (sup is null && sub is null)
            {
                return baseNode;
            }

            var limits = baseNode is MathText { Text: "∑" or "∏" or "∐" or "⋃" or "⋂" or "⨁" or "⨂" or "⋁" or "⋀" } ||
                         baseNode is LimitName;
            return new MathScripts(baseNode is LimitName named ? named.Inner : baseNode, sup, sub, limits);
        }

        private void SkipSpaces()
        {
            while (position < source.Length && char.IsWhiteSpace(source[position]))
            {
                position++;
            }
        }

        /// <summary>One argument: a braced group or a single token (a letter, a digit or a command).</summary>
        private MathNode ParseArgument()
        {
            SkipSpaces();
            if (position >= source.Length)
            {
                throw new FormatException("argument");
            }

            if (source[position] == '{')
            {
                position++;
                var row = ParseRow('\0');
                Expect('}');
                return row;
            }

            return ParseAtom();
        }

        private void Expect(char c)
        {
            if (position >= source.Length || source[position] != c)
            {
                throw new FormatException("expected " + c);
            }

            position++;
        }

        private string ReadRawGroup()
        {
            SkipSpaces();
            Expect('{');
            var start = position;
            var level = 1;
            while (position < source.Length)
            {
                if (source[position] == '{')
                {
                    level++;
                }
                else if (source[position] == '}' && --level == 0)
                {
                    var text = source[start..position];
                    position++;
                    return text;
                }

                position++;
            }

            throw new FormatException("group");
        }

        private MathNode ParseAtom()
        {
            // Arguments recurse through here without passing a row (racracrac...): count them too.
            if (++depth > 40)
            {
                throw new FormatException("nesting");
            }

            try
            {
                return ParseAtomCore();
            }
            finally
            {
                depth--;
            }
        }

        private MathNode ParseAtomCore()
        {
            var c = source[position];
            switch (c)
            {
                case '{':
                {
                    position++;
                    var row = ParseRow('\0');
                    Expect('}');
                    return row;
                }

                case '\\':
                    return ParseCommand();
                case '^' or '_' or '}' or '&' or '$' or '#' or '%':
                    throw new FormatException("misplaced " + c);
                case '~':
                    position++;
                    return new MathSpace(0.3);
            }

            position++;
            if (char.IsLetter(c))
            {
                return new MathText(c.ToString(), italic: true);
            }

            if (char.IsDigit(c))
            {
                var start = position - 1;
                while (position < source.Length && (char.IsDigit(source[position]) || source[position] == '.' &&
                           position + 1 < source.Length && char.IsDigit(source[position + 1])))
                {
                    position++;
                }

                return new MathText(source[start..position], italic: false);
            }

            return c switch
            {
                '-' => new MathText("−", italic: false),
                '*' => new MathText("∗", italic: false),
                '=' or '<' or '>' or '+' => new BinaryText(c.ToString()),
                _ => new MathText(c.ToString(), italic: false),
            };
        }

        private MathNode ParseCommand()
        {
            position++;
            if (position >= source.Length)
            {
                throw new FormatException("command");
            }

            if (!char.IsLetter(source[position]))
            {
                var symbol = source[position++];
                return symbol switch
                {
                    ',' => new MathSpace(0.17),
                    ':' or ';' => new MathSpace(0.22),
                    '!' => new MathSpace(-0.1),
                    ' ' => new MathSpace(0.3),
                    '{' or '}' or '%' or '$' or '#' or '&' or '_' => new MathText(symbol.ToString(), italic: false),
                    '|' => new MathText("‖", italic: false),
                    _ => throw new FormatException("symbol " + symbol),
                };
            }

            var start = position;
            while (position < source.Length && char.IsLetter(source[position]))
            {
                position++;
            }

            var name = source[start..position];
            switch (name)
            {
                case "frac" or "dfrac" or "tfrac":
                {
                    var numerator = ParseArgument();
                    var denominator = ParseArgument();
                    return new MathFraction(numerator, denominator);
                }

                case "binom":
                {
                    var top = ParseArgument();
                    var bottom = ParseArgument();
                    return new MathFenced("(", new MathMatrix([[top], [bottom]], string.Empty, string.Empty, cases: false), ")");
                }

                case "sqrt":
                {
                    MathNode? index = null;
                    SkipSpaces();
                    if (position < source.Length && source[position] == '[')
                    {
                        position++;
                        index = ParseRow(']');
                        Expect(']');
                    }

                    return new MathRoot(ParseArgument(), index);
                }

                case "text" or "mathrm" or "textrm" or "mbox" or "operatorname" or "mathit" or "textit":
                    return new MathText(ReadRawGroup(), italic: name is "mathit" or "textit");
                case "mathbf" or "boldsymbol" or "bm" or "textbf":
                    return new MathText(ReadRawGroup(), italic: false, bold: true);
                case "mathbb":
                {
                    var raw = ReadRawGroup();
                    var builder = new System.Text.StringBuilder();
                    foreach (var letter in raw)
                    {
                        builder.Append(Blackboard.TryGetValue(letter, out var glyph) ? glyph : letter.ToString());
                    }

                    return new MathText(builder.ToString(), italic: false);
                }

                case "mathcal" or "mathscr" or "mathfrak" or "mathsf" or "mathtt":
                    return new MathText(ReadRawGroup(), italic: name is "mathcal" or "mathscr");
                case "overline" or "bar" or "vec" or "hat" or "tilde" or "dot" or "ddot" or "widehat" or "underline":
                    return Accent(name, ParseArgument());
                case "quad":
                    return new MathSpace(1);
                case "qquad":
                    return new MathSpace(2);
                case "left":
                    return ParseFenced();
                case "begin":
                    return ParseEnvironment();
                case "displaystyle" or "textstyle" or "scriptstyle" or "limits" or "nolimits" or "big" or "Big" or "bigg" or "Bigg":
                    return new MathSpace(0);
            }

            if (BigGlyphs.TryGetValue(name, out var big))
            {
                return new MathText(big, italic: false, bold: false);
            }

            if (Functions.TryGetValue(name, out var function))
            {
                var text = new MathText(function, italic: false);
                return LimitFunctions.Contains(function) ? new LimitName(text) : text;
            }

            if (Symbols.TryGetValue(name, out var glyph2))
            {
                var greekLower = glyph2.Length == 1 && glyph2[0] is >= 'α' and <= 'ω' or 'ϑ' or 'ϕ' or 'ϖ' or 'ϱ' or 'ϵ';
                return name is "le" or "leq" or "ge" or "geq" or "ne" or "neq" or "approx" or "equiv" or "sim" or "simeq" or "cong"
                    or "propto" or "ll" or "gg" or "in" or "notin" or "ni" or "subset" or "supset" or "subseteq" or "supseteq" or "to"
                    or "rightarrow" or "leftarrow" or "gets" or "leftrightarrow" or "Rightarrow" or "Leftarrow" or "Leftrightarrow"
                    or "implies" or "iff" or "mapsto" or "pm" or "mp" or "times" or "div" or "cdot" or "oplus" or "otimes" or "cap"
                    or "cup" or "wedge" or "vee" or "land" or "lor" or "setminus" or "perp" or "parallel" or "mid"
                    ? new BinaryText(glyph2)
                    : new MathText(glyph2, italic: greekLower);
            }

            throw new FormatException("command " + name);
        }

        private static MathNode Accent(string name, MathNode body)
        {
            var mark = name switch
            {
                "overline" or "bar" => "‾",
                "underline" => "_",
                "vec" => "→",
                "hat" or "widehat" => "^",
                "tilde" => "~",
                "dot" => "˙",
                _ => "¨",
            };
            return new MathAccent(body, mark);
        }

        private MathNode ParseFenced()
        {
            var open = ReadDelimiter();
            var body = ParseRow('\0');
            if (!Peek("\\right"))
            {
                throw new FormatException("right");
            }

            position += "\\right".Length;
            var close = ReadDelimiter();
            return new MathFenced(open, body, close);
        }

        private string ReadDelimiter()
        {
            SkipSpaces();
            if (position >= source.Length)
            {
                throw new FormatException("delimiter");
            }

            var c = source[position++];
            if (c != '\\')
            {
                return c == '.' ? string.Empty : c.ToString();
            }

            if (position < source.Length && !char.IsLetter(source[position]))
            {
                var symbol = source[position++];
                return symbol is '{' or '}' ? symbol.ToString() : symbol == '|' ? "‖" : throw new FormatException("delimiter");
            }

            var start = position;
            while (position < source.Length && char.IsLetter(source[position]))
            {
                position++;
            }

            return Symbols.TryGetValue(source[start..position], out var glyph) ? glyph : throw new FormatException("delimiter");
        }

        private MathNode ParseEnvironment()
        {
            var name = ReadRawGroup();
            var (open, close, cases) = name switch
            {
                "matrix" or "array" or "aligned" or "align" or "align*" or "gathered" or "split" => (string.Empty, string.Empty, false),
                "pmatrix" => ("(", ")", false),
                "bmatrix" => ("[", "]", false),
                "Bmatrix" => ("{", "}", false),
                "vmatrix" => ("|", "|", false),
                "Vmatrix" => ("‖", "‖", false),
                "cases" => ("{", string.Empty, true),
                _ => throw new FormatException("environment " + name),
            };
            if (name == "array")
            {
                SkipSpaces();
                if (position < source.Length && source[position] == '{')
                {
                    ReadRawGroup();
                }
            }

            var rows = new List<List<MathNode>>();
            var current = new List<MathNode>();
            while (true)
            {
                current.Add(ParseRow('\0'));
                if (position >= source.Length)
                {
                    throw new FormatException("end");
                }

                if (source[position] == '&')
                {
                    position++;
                    continue;
                }

                if (Peek("\\\\"))
                {
                    position += 2;
                    rows.Add(current);
                    current = [];
                    continue;
                }

                if (Peek("\\end"))
                {
                    position += "\\end".Length;
                    if (ReadRawGroup() != name)
                    {
                        throw new FormatException("end name");
                    }

                    if (current.Count > 1 || current[0] is not MathRow { Items.Count: 0 })
                    {
                        rows.Add(current);
                    }

                    break;
                }

                throw new FormatException("environment");
            }

            var matrix = new MathMatrix(rows, string.Empty, string.Empty, cases);
            return open.Length == 0 && close.Length == 0 ? matrix : new MathFenced(open, matrix, close);
        }
    }

    /// <summary>A relation or binary operator: drawn with space around it.</summary>
    internal sealed class BinaryText(string text) : MathNode
    {
        public string Text { get; } = text;
    }

    /// <summary>A function name whose limits sit below it in display style (lim, max, min…).</summary>
    internal sealed class LimitName(MathText inner) : MathNode
    {
        public MathText Inner { get; } = inner;
    }

    /// <summary>A mark over (or under) a body: a bar, an arrow, a hat.</summary>
    internal sealed class MathAccent(MathNode body, string mark) : MathNode
    {
        public MathNode Body { get; } = body;

        public string Mark { get; } = mark;
    }
}

/// <summary>A laid-out piece of a formula: its size relative to the baseline and how to draw it.</summary>
internal abstract class MathBox
{
    /// <summary>Width in device-independent pixels.</summary>
    public double Width { get; protected set; }

    /// <summary>Distance from the baseline up to the top.</summary>
    public double Ascent { get; protected set; }

    /// <summary>Distance from the baseline down to the bottom.</summary>
    public double Descent { get; protected set; }

    /// <summary>Draws the box with its left edge at <paramref name="x"/> and its baseline at <paramref name="baseline"/>.</summary>
    public abstract void Draw(DrawingContext context, double x, double baseline);
}

/// <summary>Builds <see cref="MathBox"/> trees for a node.</summary>
internal sealed class MathBuilder(Typeface regular, IBrush? brush)
{
    private readonly Typeface upright = regular;
    private readonly Typeface italic = new(regular.FontFamily, FontStyle.Italic, FontWeight.Normal);
    private readonly Typeface bold = new(regular.FontFamily, FontStyle.Normal, FontWeight.Bold);
    private readonly IBrush? brush = brush;

    internal MathBox Build(MathNode node, double size, bool displayStyle) => node switch
    {
        MathRow row => Row(row, size, displayStyle),
        MathParser.BinaryText binary => Row([Space(size * 0.22), Glyph(binary.Text, upright, size), Space(size * 0.22)]),
        MathText text => Glyph(text.Text, text.Bold ? bold : text.Italic ? italic : upright, text.Text is "∑" or "∏" or "∐" or "∫" or "∬" or "∭" or "∮" or "⋃" or "⋂" or "⨁" or "⨂" or "⋁" or "⋀" ? size * (displayStyle ? 1.45 : 1.15) : size),
        MathSpace space => Space(space.Ems * size),
        MathScripts scripts => Scripts(scripts, size, displayStyle),
        MathFraction fraction => Fraction(fraction, size, displayStyle),
        MathRoot root => Root(root, size, displayStyle),
        MathFenced fenced => Fenced(fenced, size, displayStyle),
        MathMatrix matrix => Matrix(matrix, size),
        MathParser.MathAccent accent => Accent(accent, size, displayStyle),
        MathParser.LimitName name => Build(name.Inner, size, displayStyle),
        _ => Space(0),
    };

    private MathBox Row(MathRow row, double size, bool displayStyle)
    {
        var boxes = new List<MathBox>(row.Items.Count);
        foreach (var item in row.Items)
        {
            boxes.Add(Build(item, size, displayStyle));
        }

        return Row(boxes);
    }

    private static HorizontalBox Row(List<MathBox> boxes) => new(boxes);


    private SpaceBox Space(double width) => new(width);

    private GlyphBox Glyph(string text, Typeface face, double size) => new(text, face, size, brush);

    private MathBox Scripts(MathScripts node, double size, bool displayStyle)
    {
        var baseBox = Build(node.Base, size, displayStyle);
        var small = size * 0.7;
        var sup = node.Sup is null ? null : Build(node.Sup, small, false);
        var sub = node.Sub is null ? null : Build(node.Sub, small, false);
        if (node.Limits && displayStyle)
        {
            return new LimitsBox(baseBox, sup, sub, size);
        }

        return new ScriptsBox(baseBox, sup, sub, size);
    }

    private MathBox Fraction(MathFraction node, double size, bool displayStyle)
    {
        var childSize = displayStyle ? size : size * 0.8;
        return new FractionBox(Build(node.Numerator, childSize, false), Build(node.Denominator, childSize, false), size, brush);
    }

    private MathBox Root(MathRoot node, double size, bool displayStyle)
    {
        var body = Build(node.Body, size, displayStyle);
        var index = node.Index is null ? null : Build(node.Index, size * 0.6, false);
        return new RootBox(body, index, size, brush);
    }

    private MathBox Fenced(MathFenced node, double size, bool displayStyle)
    {
        var body = Build(node.Body, size, displayStyle);
        return Row(
        [
            new DelimiterBox(node.Open, body, size, upright, brush),
            body,
            new DelimiterBox(node.Close, body, size, upright, brush),
        ]);
    }

    private MathBox Matrix(MathMatrix node, double size)
    {
        var cells = new List<List<MathBox>>(node.Rows.Count);
        foreach (var row in node.Rows)
        {
            var built = new List<MathBox>(row.Count);
            foreach (var cell in row)
            {
                built.Add(Build(cell, size, false));
            }

            cells.Add(built);
        }

        return new MatrixBox(cells, size, node.Cases);
    }

    private MathBox Accent(MathParser.MathAccent node, double size, bool displayStyle)
    {
        var body = Build(node.Body, size, displayStyle);
        return new AccentBox(body, node.Mark, size, brush);
    }

    private sealed class SpaceBox : MathBox
    {
        internal SpaceBox(double width) => Width = width;

        public override void Draw(DrawingContext context, double x, double baseline)
        {
        }
    }

    private sealed class GlyphBox : MathBox
    {
        private readonly FormattedText text;

        internal GlyphBox(string value, Typeface face, double size, IBrush? brush)
        {
            text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush);
            Width = text.WidthIncludingTrailingWhitespace;
            Ascent = text.Baseline;
            Descent = Math.Max(0, text.Height - text.Baseline);
        }

        public override void Draw(DrawingContext context, double x, double baseline) =>
            context.DrawText(text, new Point(x, baseline - Ascent));
    }

    private sealed class HorizontalBox : MathBox
    {
        private readonly List<MathBox> boxes;

        internal HorizontalBox(List<MathBox> boxes)
        {
            this.boxes = boxes;
            foreach (var box in boxes)
            {
                Width += box.Width;
                Ascent = Math.Max(Ascent, box.Ascent);
                Descent = Math.Max(Descent, box.Descent);
            }
        }

        public override void Draw(DrawingContext context, double x, double baseline)
        {
            foreach (var box in boxes)
            {
                box.Draw(context, x, baseline);
                x += box.Width;
            }
        }
    }

    private sealed class ScriptsBox : MathBox
    {
        private readonly MathBox body;
        private readonly MathBox? sup;
        private readonly MathBox? sub;
        private readonly double supShift;
        private readonly double subShift;

        internal ScriptsBox(MathBox body, MathBox? sup, MathBox? sub, double size)
        {
            this.body = body;
            this.sup = sup;
            this.sub = sub;
            supShift = Math.Max(body.Ascent * 0.62, size * 0.36);
            subShift = Math.Max(body.Descent + size * 0.12, size * 0.2);

            Width = body.Width + Math.Max(sup?.Width ?? 0, sub?.Width ?? 0) + size * 0.04;
            Ascent = Math.Max(body.Ascent, sup is null ? 0 : supShift + sup.Ascent);
            Descent = Math.Max(body.Descent, sub is null ? 0 : subShift + sub.Descent);
        }

        public override void Draw(DrawingContext context, double x, double baseline)
        {
            body.Draw(context, x, baseline);
            var scriptX = x + body.Width + 1;
            sup?.Draw(context, scriptX, baseline - supShift);
            sub?.Draw(context, scriptX, baseline + subShift);
        }
    }

    private sealed class LimitsBox : MathBox
    {
        private readonly MathBox body;
        private readonly MathBox? above;
        private readonly MathBox? below;
        private readonly double aboveShift;
        private readonly double belowShift;

        internal LimitsBox(MathBox body, MathBox? above, MathBox? below, double size)
        {
            this.body = body;
            this.above = above;
            this.below = below;
            var gap = size * 0.12;
            Width = Math.Max(body.Width, Math.Max(above?.Width ?? 0, below?.Width ?? 0));
            aboveShift = body.Ascent + gap + (above?.Descent ?? 0);
            belowShift = body.Descent + gap + (below?.Ascent ?? 0);
            Ascent = above is null ? body.Ascent : aboveShift + above.Ascent;
            Descent = below is null ? body.Descent : belowShift + below.Descent;
        }

        public override void Draw(DrawingContext context, double x, double baseline)
        {
            body.Draw(context, x + (Width - body.Width) / 2, baseline);
            above?.Draw(context, x + (Width - above.Width) / 2, baseline - aboveShift);
            below?.Draw(context, x + (Width - below.Width) / 2, baseline + belowShift);
        }
    }

    private sealed class FractionBox : MathBox
    {
        private readonly MathBox numerator;
        private readonly MathBox denominator;
        private readonly double axis;
        private readonly Pen pen;

        internal FractionBox(MathBox numerator, MathBox denominator, double size, IBrush? brush)
        {
            this.numerator = numerator;
            this.denominator = denominator;
            axis = size * 0.28;
            var gap = size * 0.14;
            pen = new Pen(brush, Math.Max(1, size * 0.06));
            Width = Math.Max(numerator.Width, denominator.Width) + size * 0.3;
            Ascent = axis + gap + numerator.Descent + numerator.Ascent;
            Descent = Math.Max(0, denominator.Ascent + denominator.Descent + gap - axis);
        }

        public override void Draw(DrawingContext context, double x, double baseline)
        {
            var barY = baseline - axis;
            var gap = pen.Thickness + 2;
            numerator.Draw(context, x + (Width - numerator.Width) / 2, barY - gap - numerator.Descent);
            denominator.Draw(context, x + (Width - denominator.Width) / 2, barY + gap + denominator.Ascent);
            context.DrawLine(pen, new Point(x + 1, barY), new Point(x + Width - 1, barY));
        }
    }

    private sealed class RootBox : MathBox
    {
        private readonly MathBox body;
        private readonly MathBox? index;
        private readonly double size;
        private readonly double tick;
        private readonly Pen pen;

        internal RootBox(MathBox body, MathBox? index, double size, IBrush? brush)
        {
            this.body = body;
            this.index = index;
            this.size = size;
            pen = new Pen(brush, Math.Max(1, size * 0.07));
            tick = size * 0.6 + Math.Max(0, (index?.Width ?? 0) - size * 0.25);
            Width = tick + body.Width + size * 0.1;
            Ascent = body.Ascent + size * 0.22;
            Descent = body.Descent;
        }

        public override void Draw(DrawingContext context, double x, double baseline)
        {
            var top = baseline - Ascent + pen.Thickness / 2;
            var bottom = baseline + body.Descent;
            var left = x + (index is null ? 0 : Math.Max(0, index.Width - size * 0.25));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(left, baseline - size * 0.2), false);
                g.LineTo(new Point(left + size * 0.15, baseline - size * 0.28));
                g.LineTo(new Point(left + size * 0.3, bottom));
                g.LineTo(new Point(left + size * 0.52, top));
                g.LineTo(new Point(x + Width, top));
                g.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
            index?.Draw(context, x, top + index.Ascent + size * 0.35);
            body.Draw(context, x + tick, baseline);
        }
    }

    private sealed class DelimiterBox : MathBox
    {
        private readonly string text;
        private readonly FormattedText glyph;

        internal DelimiterBox(string text, MathBox body, double size, Typeface face, IBrush? brush)
        {
            this.text = text;
            var natural = new FormattedText(text.Length == 0 ? " " : text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush);
            var wanted = body.Ascent + body.Descent;
            var scale = Math.Max(1, Math.Min(3.2, wanted / Math.Max(1, natural.Height)));
            var fontSize = size * scale;
            glyph = new FormattedText(text.Length == 0 ? " " : text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, fontSize, brush);
            Width = text.Length == 0 ? 0 : glyph.WidthIncludingTrailingWhitespace;
            Ascent = Math.Max(body.Ascent, glyph.Baseline * 0.9);
            Descent = Math.Max(body.Descent, glyph.Height - glyph.Baseline);
        }

        public override void Draw(DrawingContext context, double x, double baseline)
        {
            if (text.Length == 0)
            {
                return;
            }

            // Centre the (scaled) glyph on the body's own vertical middle.
            var middle = baseline - (Ascent - Descent) / 2;
            context.DrawText(glyph, new Point(x, middle - glyph.Height / 2));
        }
    }

    private sealed class MatrixBox : MathBox
    {
        private readonly List<List<MathBox>> cells;
        private readonly double[] columns;
        private readonly double[] rowAscent;
        private readonly double[] rowDescent;
        private readonly double gapX;
        private readonly double gapY;
        private readonly bool leftAligned;

        internal MatrixBox(List<List<MathBox>> cells, double size, bool leftAligned)
        {
            this.cells = cells;
            this.leftAligned = leftAligned;
            gapX = size * 0.9;
            gapY = size * 0.25;
            var columnCount = cells.Count == 0 ? 0 : cells.Max(static row => row.Count);
            columns = new double[columnCount];
            rowAscent = new double[cells.Count];
            rowDescent = new double[cells.Count];
            for (var r = 0; r < cells.Count; r++)
            {
                for (var c = 0; c < cells[r].Count; c++)
                {
                    columns[c] = Math.Max(columns[c], cells[r][c].Width);
                    rowAscent[r] = Math.Max(rowAscent[r], cells[r][c].Ascent);
                    rowDescent[r] = Math.Max(rowDescent[r], cells[r][c].Descent);
                }
            }

            Width = columns.Sum() + gapX * Math.Max(0, columnCount - 1);
            var height = rowAscent.Sum() + rowDescent.Sum() + gapY * Math.Max(0, cells.Count - 1);
            // The block is centred on the math axis, like a delimiter-wrapped matrix is.
            var axis = size * 0.28;
            Ascent = height / 2 + axis;
            Descent = Math.Max(0, height / 2 - axis);
        }

        public override void Draw(DrawingContext context, double x, double baseline)
        {
            var y = baseline - Ascent;
            for (var r = 0; r < cells.Count; r++)
            {
                y += rowAscent[r];
                var cx = x;
                for (var c = 0; c < cells[r].Count; c++)
                {
                    var cell = cells[r][c];
                    cell.Draw(context, cx + (leftAligned ? 0 : (columns[c] - cell.Width) / 2), y);
                    cx += columns[c] + gapX;
                }

                y += rowDescent[r] + gapY;
            }
        }
    }

    private sealed class AccentBox : MathBox
    {
        private readonly MathBox body;
        private readonly FormattedText? mark;
        private readonly Pen? line;
        private readonly bool under;

        internal AccentBox(MathBox body, string markText, double size, IBrush? brush)
        {
            this.body = body;
            Width = body.Width;
            Ascent = body.Ascent;
            Descent = body.Descent;
            if (markText is "‾" or "_")
            {
                line = new Pen(brush, Math.Max(1, size * 0.06));
                under = markText == "_";
                if (under)
                {
                    Descent += size * 0.15;
                }
                else
                {
                    Ascent += size * 0.15;
                }
            }
            else
            {
                mark = new FormattedText(markText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Cambria Math, Segoe UI Symbol"), size, brush);
                Ascent += size * 0.25;
            }
        }

        public override void Draw(DrawingContext context, double x, double baseline)
        {
            body.Draw(context, x, baseline);
            if (line is not null)
            {
                var y = under ? baseline + body.Descent + line.Thickness : baseline - body.Ascent - line.Thickness;
                context.DrawLine(line, new Point(x, y), new Point(x + Width, y));
            }
            else if (mark is not null)
            {
                context.DrawText(mark, new Point(x + (Width - mark.Width) / 2, baseline - body.Ascent - mark.Height * 0.55));
            }
        }
    }
}
