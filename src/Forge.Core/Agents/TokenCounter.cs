using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>Budget check for compaction; counts text as roughly four characters per token.</summary>
public interface ITokenCounter
{
    int Count(IReadOnlyList<ChatMessage> messages);

    int Count(string text);
}

public sealed class ApproximateTokenCounter : ITokenCounter
{
    private const int MessageOverhead = 4;
    private const int CharsPerToken = 4;

    public int Count(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        long chars = 0;
        foreach (var message in messages)
        {
            foreach (var block in message.Content)
            {
                chars += block switch
                {
                    TextBlock text => text.Text.Length,
                    ThinkingBlock thinking => thinking.Text.Length,
                    ToolCallBlock call => call.Name.Length + call.ArgumentsJson.Length,
                    ToolResultBlock result => result.Content.Length,
                    ArtifactRefBlock artifact => artifact.Description.Length + 32,
                    _ => 0,
                };
            }
        }

        return Saturate(chars / CharsPerToken + (long)messages.Count * MessageOverhead);
    }

    public int Count(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return Saturate((text.Length + CharsPerToken - 1) / CharsPerToken);
    }

    private static int Saturate(long value) => (int)Math.Min(int.MaxValue, value);
}
