using System.Text.Json;
using Forge.Abstractions;

namespace Forge.Workspace;

/// <summary>Changes the title and/or body of the current branch's pull request through <see cref="PrBodyEditor"/>.</summary>
internal sealed class PrEditTool(GitCli git, IPrPlatform platform) : PrLifecycleToolBase(
    git,
    platform,
    PrTools.EditName,
    "Change the title and/or description of the pull request of the current branch. Prefer section: replace only the " +
    "'## heading' section and leave the rest of the body alone; body replaces the whole description (give body or " +
    "section, not both). The current title and body are read first and read again right before writing: a section edit " +
    "is applied onto the newest body if it changed meanwhile, a full replacement is refused when it did (both versions " +
    "are shown). After the write the pull request is read back and compared; a mismatch is an error, so do not assume " +
    "an edit worked unless the result says it was verified. Read pr_workflow with phase 'body' first.",
    """
    {"type":"object","properties":{"title":{"type":"string","description":"New title (one line)."},"body":{"type":"string","description":"New full description. Replaces everything; prefer section."},"section":{"type":"object","properties":{"heading":{"type":"string","description":"Heading of the level-2 section, e.g. Testing."},"content":{"type":"string","description":"New content of that section, without the heading line."}},"required":["heading","content"],"additionalProperties":false,"description":"Replace one '## heading' section; it is appended when the body has none."}},"additionalProperties":false}
    """,
    ToolSideEffect.Write)
{
    protected override async ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        GitToolJson.EnsureObject(arguments, "title", "body", "section");

        var title = GitToolJson.String(arguments, "title");
        var body = GitToolJson.String(arguments, "body");
        var section = ReadSection(arguments);

        if (title is null && body is null && section is null)
        {
            return new ToolResult("Give at least one of title, body or section.", IsError: true);
        }

        if (body is not null && section is not null)
        {
            return new ToolResult("Give body or section, not both: body replaces everything, section replaces one part.", IsError: true);
        }

        if (title is not null && (title.Trim().Length == 0 || title.Contains('\n', StringComparison.Ordinal)))
        {
            return new ToolResult("title must be a single non-blank line.", IsError: true);
        }

        if (body is not null && body.Trim().Length == 0)
        {
            return new ToolResult(
                "body must not be blank; to change one part of the description use section instead.",
                IsError: true);
        }

        if (await RequireReadyAsync(context.WorkingDirectory, ct).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        var result = await new PrBodyEditor(Platform).EditAsync(context.WorkingDirectory, title, body, section, null, ct).ConfigureAwait(false);
        return result.Success
            ? new ToolResult(result.Status.Message)
            : PrLifecycleFailure.FromPlatform(result);
    }

    private static PrSectionEdit? ReadSection(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("section", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("'section' must be an object with 'heading' and 'content'.");
        }

        GitToolJson.EnsureObject(value, "heading", "content");
        var heading = GitToolJson.RequiredString(value, "heading");
        var content = GitToolJson.String(value, "content")
            ?? throw new ArgumentException("'section.content' must be a string (empty clears the section).");
        if (heading.Trim().Trim('#').Trim().Length == 0)
        {
            throw new ArgumentException("'section.heading' must name a section, for example Testing.");
        }

        return new PrSectionEdit(heading, content);
    }
}
