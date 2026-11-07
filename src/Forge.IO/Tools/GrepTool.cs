using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// <c>grep</c>: matching lines of the files below a directory (or of one file), as <c>path:line:text</c>, with
/// <c>path-line-text</c> for context lines. The managed engine is the default; when <see cref="ToolOptions.RipgrepPath"/>
/// names an existing executable the call goes through <see cref="RipgrepBackend"/> first and falls back to the managed
/// engine, with the reason, whenever ripgrep cannot be trusted. A query without a hit returns <c>no matches</c> and is
/// not an error.
/// </summary>
public sealed class GrepTool : ToolBase
{
    private const int MaxContextLines = ToolOptions.MaxGrepContextLines;

    private const string Schema =
        """{"type":"object","properties":{"pattern":{"type":"string","description":"Text to find, or a .NET regular expression when `regex` is true."},"path":{"type":"string","description":"Directory or file to search, relative to the workspace root; default the root."},"glob":{"type":"string","description":"Only search files whose workspace-relative path matches this glob: `*` inside one segment, `**` across segments, `?`, `[...]`. A glob without `/` matches a name at any depth."},"regex":{"type":"boolean","description":"Treat `pattern` as a .NET regular expression; default false."},"case_sensitive":{"type":"boolean","description":"Case-sensitive matching; default true."},"context":{"type":"integer","description":"Context lines before and after each match; default 0, at most 5."},"limit":{"type":"integer","description":"Maximum matching lines; default 1000, at most 10000. Hitting it marks the output as truncated."}},"required":["pattern"],"additionalProperties":false}""";

    private readonly Func<ToolOptions> options;
    private readonly IProcessRunner? runner;

    public GrepTool()
        : this(ToolOptions.Default, null)
    {
    }

    public GrepTool(ToolOptions options, IProcessRunner? runner = null)
        : this(Fixed(options), runner)
    {
    }

    public GrepTool(Func<ToolOptions> options, IProcessRunner? runner = null)
        : base(
            "grep",
            "Search files for a literal string or a regular expression, one `path:line:text` line per matching line and "
            + "`path-line-text` for context lines. Applies .gitignore; skips binary and oversized files and reports how "
            + "many. Returns `no matches` (not an error) when nothing matches.",
            Schema,
            ToolSideEffect.ReadOnly)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        this.runner = runner;
    }

    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        var bounds = options();
        var pattern = ToolJson.RequiredString(arguments, "pattern");
        var requested = ToolJson.OptionalString(arguments, "path") ?? ".";
        var glob = NormalizeGlob(ToolJson.OptionalString(arguments, "glob"));
        var regex = ToolJson.OptionalBool(arguments, "regex", false);
        var caseSensitive = ToolJson.OptionalBool(arguments, "case_sensitive", true);
        var contextLines = ToolJson.OptionalInt(arguments, "context", 0, 0, MaxContextLines);
        var limit = ToolJson.OptionalInt(
            arguments,
            "limit",
            Math.Clamp(bounds.GrepMaxResults, 1, ToolOptions.MaxGrepMaxResults),
            1,
            ToolOptions.MaxGrepMaxResults);

        var root = WorkspacePath.ResolveRealPath(Root(context));
        var start = WorkspacePath.Resolve(root, requested);
        if (!Directory.Exists(start) && !File.Exists(start))
        {
            return new ToolResult($"grep: '{requested}' does not exist.", IsError: true);
        }

        var request = new GrepRequest
        {
            Root = root,
            StartPath = start,
            Pattern = pattern,
            Glob = glob,
            Regex = regex,
            CaseSensitive = caseSensitive,
            ContextLines = contextLines,
            Limit = limit,
            MaxFileBytes = Math.Max(1, bounds.GrepMaxFileBytes),
            Concurrency = bounds.SearchConcurrency,
        };

        GrepResult result;
        string? fallback = null;
        if (runner is not null && RipgrepBackend.IsAvailable(bounds.RipgrepPath))
        {
            var outcome = await RipgrepBackend
                .SearchAsync(request, bounds.RipgrepPath!, runner, ct)
                .ConfigureAwait(false);
            if (outcome is { Succeeded: true, Result: { } fast })
            {
                result = fast;
            }
            else
            {
                fallback = outcome.FailureReason ?? "ripgrep was not used";
                result = await GrepEngine.SearchAsync(request, ct).ConfigureAwait(false);
            }
        }
        else
        {
            result = await GrepEngine.SearchAsync(request, ct).ConfigureAwait(false);
        }

        return new ToolResult(Format(result, limit, request.MaxFileBytes, fallback));
    }

    private static string Format(GrepResult result, int limit, int maxFileBytes, string? fallback)
    {
        var lines = new List<string>(result.Lines.Count + 4);
        foreach (var line in result.Lines)
        {
            var separator = line.IsMatch ? ':' : '-';
            var builder = new StringBuilder(line.Path.Length + line.Text.Length + 16);
            builder.Append(line.Path).Append(separator).Append(line.LineNumber).Append(separator).Append(line.Text);
            lines.Add(builder.ToString());
        }

        if (lines.Count == 0)
        {
            lines.Add("no matches");
        }

        if (result.Truncated)
        {
            lines.Add($"… (truncated at {limit} matches)");
        }

        if (result.FilesSkippedTooLarge > 0)
        {
            lines.Add($"{result.FilesSkippedTooLarge} file(s) skipped: larger than {maxFileBytes} bytes");
        }

        if (result.FilesSkippedBinary > 0)
        {
            lines.Add($"{result.FilesSkippedBinary} file(s) skipped: binary");
        }

        foreach (var file in result.RegexTimeoutFiles)
        {
            lines.Add($"regex match timeout: {file}");
        }

        if (fallback is not null)
        {
            lines.Add($"ripgrep not used: {fallback}");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Drops a leading separator: a glob with a separator is already anchored to the workspace root, and both backends
    /// agree only on the form without it.
    /// </summary>
    private static string? NormalizeGlob(string? glob)
    {
        if (string.IsNullOrWhiteSpace(glob))
        {
            return null;
        }

        var value = glob.Trim();
        return value.TrimStart('/') is { Length: > 0 } trimmed ? trimmed : null;
    }

    private static Func<ToolOptions> Fixed(ToolOptions options) => () => options;
}
