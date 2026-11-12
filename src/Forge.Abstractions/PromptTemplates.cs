namespace Forge.Abstractions;

/// <summary>What a prompt template is for.</summary>
public enum PromptTemplateKind
{
    /// <summary>A slash command the user types (<c>/name args</c>); its body is expanded into the message that is sent.</summary>
    Command,

    /// <summary>An agent kind: role prompt plus optional model, thinking and tool whitelist.</summary>
    Agent,

    /// <summary>A fixed, named built-in prompt (system prompt, compaction summarizer, ...). Users can override, never add.</summary>
    Slot,
}

/// <summary>Where a template comes from, in ascending precedence: a higher scope replaces or extends a lower one.</summary>
public enum PromptTemplateScope
{
    BuiltIn = 0,
    Global = 1,
    Project = 2,
}

/// <summary>How a file combines with the lower template of the same kind and id.</summary>
public enum PromptTemplateMode
{
    /// <summary>The file is the whole template; lower ones are shadowed.</summary>
    Replace,

    /// <summary>The file's body is appended after the lower template's body; unset metadata is inherited.</summary>
    Append,
}

public enum PromptTemplateProblemCode
{
    /// <summary>The id (file name) is not 1-64 chars of <c>a-z 0-9 . _ -</c> starting with a letter or digit.</summary>
    InvalidId,

    /// <summary>The file opens a <c>---</c> frontmatter block that never closes.</summary>
    UnclosedFrontmatter,

    /// <summary>A frontmatter key this kind does not know. Detail: the key.</summary>
    UnknownKey,

    /// <summary>A frontmatter value that cannot be read (bad <c>mode</c>, <c>thinking</c>, ...). Detail: the key.</summary>
    InvalidValue,

    /// <summary>The body is empty.</summary>
    EmptyBody,

    /// <summary>A slot file whose id is not a built-in slot. Slots can be overridden, not added.</summary>
    UnknownSlot,

    /// <summary>A <c>{name}</c> placeholder the slot does not provide. Detail: the name.</summary>
    UnknownPlaceholder,

    /// <summary>The file is larger than the store accepts (256 KiB).</summary>
    TooLarge,

    /// <summary>The file could not be read or is not valid UTF-8. Detail: the message.</summary>
    Unreadable,
}

/// <summary>One validation problem; the UI turns <see cref="Code"/> into localized text.</summary>
public sealed record PromptTemplateProblem(PromptTemplateProblemCode Code, string? Detail = null);

/// <summary>Address of one template: kind + scope + id, never a path.</summary>
public sealed record PromptTemplateRef(PromptTemplateKind Kind, PromptTemplateScope Scope, string Id);

/// <summary>
/// One row of the template listing. Every built-in and every file found is listed, including ones a higher scope
/// shadows; <see cref="Problems"/> is non-empty for a file that is not used because it is invalid.
/// </summary>
public sealed record PromptTemplateEntry(
    PromptTemplateRef Ref,
    string DisplayName,
    string Description,
    PromptTemplateMode Mode,
    string? Path,
    bool IsShadowed,
    bool HasBuiltIn,
    IReadOnlyList<PromptTemplateProblem> Problems)
{
    public bool IsBuiltIn => Ref.Scope == PromptTemplateScope.BuiltIn;

    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// The raw text of a template as stored. <see cref="Revision"/> identifies the bytes read; it is null for a built-in.
/// Pass it back to <see cref="IPromptTemplateStore.Save"/> so a file changed on disk meanwhile is a conflict, not lost.
/// </summary>
public sealed record PromptTemplateText(PromptTemplateRef Ref, string Text, string? Revision, string? Path);

public enum PromptTemplateSaveStatus
{
    Saved,

    /// <summary>The file changed (or appeared/disappeared) since it was read; nothing was written.</summary>
    Conflict,

    /// <summary>The text fails validation; nothing was written. See <see cref="PromptTemplateSaveResult.Problems"/>.</summary>
    Invalid,

    /// <summary>Built-in templates (and the project scope without a workspace) cannot be written.</summary>
    ReadOnly,
}

public sealed record PromptTemplateSaveResult(PromptTemplateSaveStatus Status, string? Revision, IReadOnlyList<PromptTemplateProblem> Problems);

/// <summary>
/// Reads and writes prompt templates. Built-ins are compiled in and read-only; overriding one means saving a file with
/// the same kind and id in the global (<c>~/.forge</c>) or project (<c>&lt;workspace&gt;/.forge</c>) scope, and
/// "restore default" means deleting that file. All members do file IO: never call them on the UI thread.
/// </summary>
public interface IPromptTemplateStore
{
    /// <summary>Built-ins, global files and (when <paramref name="workspace"/> is given) project files, stably ordered.</summary>
    IReadOnlyList<PromptTemplateEntry> List(string? workspace);

    /// <summary>The stored text, or null when that scope has no such template.</summary>
    PromptTemplateText? Read(PromptTemplateRef reference, string? workspace);

    /// <summary>
    /// Writes the file. <paramref name="expectedRevision"/> null means "create": it fails with
    /// <see cref="PromptTemplateSaveStatus.Conflict"/> when the file already exists.
    /// </summary>
    PromptTemplateSaveResult Save(PromptTemplateRef reference, string? workspace, string text, string? expectedRevision);

    /// <summary>Deletes the file when its bytes still match <paramref name="expectedRevision"/>.</summary>
    PromptTemplateSaveStatus Delete(PromptTemplateRef reference, string? workspace, string expectedRevision);

    /// <summary>Problems that would stop <paramref name="text"/> from being used; empty when valid.</summary>
    IReadOnlyList<PromptTemplateProblem> Validate(PromptTemplateKind kind, string id, string text);
}
