namespace Forge.Abstractions;

public enum MessageRole { System, User, Assistant, Tool }

/// <summary>Provider-independent content block.</summary>
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ThinkingBlock(string Text, string? Signature = null) : ContentBlock;

public sealed record ToolCallBlock(string Id, string Name, string ArgumentsJson) : ContentBlock;

/// <summary>The answer to one <see cref="ToolCallBlock"/>: text and images in order, like <see cref="ToolResult"/>.</summary>
public sealed record ToolResultBlock(string CallId, IReadOnlyList<ContentBlock> Blocks, bool IsError = false) : ContentBlock
{
    public ToolResultBlock(string CallId, string Content, bool IsError = false)
        : this(CallId, [new TextBlock(Content)], IsError)
    {
    }

    private readonly IReadOnlyList<ContentBlock> blocks = ToolResultBlocks.Validate(Blocks);

    /// <summary>Text and image blocks only; a <c>with</c> expression is validated too.</summary>
    public IReadOnlyList<ContentBlock> Blocks
    {
        get => blocks;
        init => blocks = ToolResultBlocks.Validate(value);
    }

    /// <summary>The flattened text of <see cref="Blocks"/>; see <see cref="ToolResultBlocks.Flatten"/>.</summary>
    public string Content => ToolResultBlocks.Flatten(Blocks);

    /// <summary>The tool's own words, without a placeholder for its images; see <see cref="ToolResultBlocks.TextOnly"/>.</summary>
    public string Text => ToolResultBlocks.TextOnly(Blocks);

    /// <summary>Whether the result carries a picture, without building a list of them.</summary>
    public bool HasImages => ToolResultBlocks.HasImage(Blocks);

    public bool Equals(ToolResultBlock? other) =>
        other is not null && CallId == other.CallId && IsError == other.IsError && ToolResultBlocks.SequenceEqual(Blocks, other.Blocks);

    public override int GetHashCode() => HashCode.Combine(CallId, IsError, ToolResultBlocks.Hash(Blocks));
}

/// <summary>
/// An image the user attached (user messages only for now). <paramref name="MediaType"/> is one of <c>image/png</c>,
/// <c>image/jpeg</c>, <c>image/gif</c>, <c>image/webp</c>; <paramref name="Data"/> is the encoded image as base64, already
/// bounded by the attaching side (long edge, byte size). Providers write it in their own wire shape; a summary or a search
/// view shows a placeholder instead of the data.
/// </summary>
public sealed record ImageBlock(string MediaType, string Data) : ContentBlock;

/// <summary>
/// A non-image binary file the user attached (user messages only): a PDF (<c>application/pdf</c>), audio
/// (<c>audio/*</c>) or video (<c>video/*</c>). <paramref name="Data"/> is the file as base64, already bounded by the attaching
/// side; <paramref name="Name"/> is the original file name when known (shown to the model where the wire format carries
/// one). Text and source files are not a <see cref="FileBlock"/>: the attaching side inlines them as a <see cref="TextBlock"/>.
/// Whether a model may receive a kind is decided from <see cref="ModelInputs"/> before sending; a provider whose wire format
/// has no shape for a kind fails the request instead of dropping the block. A summary or a search view shows a placeholder.
/// </summary>
public sealed record FileBlock(string MediaType, string Data, string? Name = null) : ContentBlock;

/// <summary>Reference to a large output stored outside the context; retrieved on demand by id.</summary>
public sealed record ArtifactRefBlock(string ArtifactId, string Description, long SizeBytes) : ContentBlock;

public sealed record ChatMessage(MessageRole Role, IReadOnlyList<ContentBlock> Content)
{
    public static ChatMessage User(string text) => new(MessageRole.User, [new TextBlock(text)]);
    public static ChatMessage Assistant(string text) => new(MessageRole.Assistant, [new TextBlock(text)]);
}

public sealed record TokenUsage(int InputTokens, int OutputTokens, int CacheReadTokens = 0, int CacheWriteTokens = 0)
{
    public static TokenUsage Empty { get; } = new(0, 0);
    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens,
            a.CacheReadTokens + b.CacheReadTokens, a.CacheWriteTokens + b.CacheWriteTokens);
}
