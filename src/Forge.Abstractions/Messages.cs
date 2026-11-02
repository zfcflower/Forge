namespace Forge.Abstractions;

public enum MessageRole { System, User, Assistant, Tool }

/// <summary>Provider-independent content block.</summary>
public abstract record ContentBlock;

public sealed record TextBlock(string Text) : ContentBlock;

public sealed record ThinkingBlock(string Text, string? Signature = null) : ContentBlock;

public sealed record ToolCallBlock(string Id, string Name, string ArgumentsJson) : ContentBlock;

public sealed record ToolResultBlock(string CallId, string Content, bool IsError = false) : ContentBlock;

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
