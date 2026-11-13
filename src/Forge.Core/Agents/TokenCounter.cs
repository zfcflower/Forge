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

    /// <summary>
    /// A flat estimate per attached image: providers bill an image by its pixel size, not by its base64 length, and a
    /// 2048px-bounded image lands near this figure. Real usage replaces the estimate once a reply reports it.
    /// </summary>
    public const int ImageTokens = 1600;

    /// <summary>
    /// Tokens per decoded kilobyte of an attached file (PDF, audio, video). A PDF is billed as its extracted text plus a page
    /// image per page, which for typical documents lands near 250 tokens per KB of file (~3 KB per page at ~1.5k tokens
    /// per page, and text-heavy PDFs compress well); audio and video are billed by duration, which this cannot see, so the
    /// same rate stands in as a conservative guess. Real usage replaces the estimate once a reply reports it.
    /// </summary>
    public const int FileTokensPerKilobyte = 250;

    /// <summary>The estimate for one attached file: <see cref="FileTokensPerKilobyte"/> per started KB, at least one KB.</summary>
    public static long FileTokens(FileBlock file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var kilobytes = Math.Max(1, (Storage.MessageJson.DecodedLength(file.Data) + 1023) / 1024);
        return kilobytes * FileTokensPerKilobyte;
    }

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
                    ToolResultBlock result => result.Content.Length + (long)result.Images.Count * ImageTokens * CharsPerToken,
                    ArtifactRefBlock artifact => artifact.Description.Length + 32,
                    ImageBlock => (long)ImageTokens * CharsPerToken,
                    FileBlock file => FileTokens(file) * CharsPerToken,
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
