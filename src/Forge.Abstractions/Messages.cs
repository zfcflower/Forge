namespace Forge.Abstractions;

public enum MessageRole { System, User, Assistant, Tool }

/// <summary>Provider-independent content block.</summary>
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ThinkingBlock(string Text, string? Signature = null) : ContentBlock;

public sealed record ToolCallBlock(string Id, string Name, string ArgumentsJson) : ContentBlock;

public sealed record ToolResultBlock : ContentBlock
{
    /// <summary>A text-only tool result; the shape every existing call site builds.</summary>
    public ToolResultBlock(string CallId, string Content, bool IsError = false)
        : this(CallId, [new TextBlock(Content)], IsError)
    {
    }

    public ToolResultBlock(string CallId, IReadOnlyList<ContentBlock> Blocks, bool IsError = false)
    {
        ArgumentNullException.ThrowIfNull(Blocks);
        this.CallId = CallId;
        this.Blocks = Blocks.Count == 0 ? [] : [.. Blocks];
        Text = ToolPayload.Text(this.Blocks);
        Content = ToolPayload.HasNonText(this.Blocks) ? ToolPayload.WithPlaceholders(this.Blocks) : Text;
        Images = ToolPayload.Images(this.Blocks);
        this.IsError = IsError;
    }

    public string CallId { get; }

    /// <summary>The blocks the tool returned: text, images, or both (see <see cref="ToolResult"/>).</summary>
    public IReadOnlyList<ContentBlock> Blocks { get; }

    /// <summary>The tool's own text, without the stand-ins for blocks that carry no text.</summary>
    public string Text { get; }

    /// <summary>
    /// The text view of <see cref="Blocks"/>, placeholders included; what a transcript, the search index and a provider that
    /// does not send the images itself sees.
    /// </summary>
    public string Content { get; }

    /// <summary>The images this result carried; a provider that cannot carry them, and every text reader, sees <see cref="Content"/> instead.</summary>
    public IReadOnlyList<ImageBlock> Images { get; }

    public bool IsError { get; }

    /// <summary>Value equality over the blocks themselves, in order (the list is an array, which compares by reference).</summary>
    public bool Equals(ToolResultBlock? other) =>
        other is not null && CallId == other.CallId && IsError == other.IsError && ToolPayload.Same(Blocks, other.Blocks);

    public override int GetHashCode() => HashCode.Combine(CallId, IsError, Blocks.Count);
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
