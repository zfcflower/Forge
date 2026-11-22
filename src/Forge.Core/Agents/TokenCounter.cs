using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>Budget check for compaction; counts text by character class, roughly four Latin characters per token.</summary>
public interface ITokenCounter
{
    int Count(IReadOnlyList<ChatMessage> messages);

    int Count(string text);
}

public sealed class ApproximateTokenCounter : ITokenCounter
{
    private const int MessageOverhead = 4;

    /// <summary>Estimate units per token: a Latin character costs two units, so four of them make one token.</summary>
    private const int UnitsPerToken = 8;

    /// <summary>One byte-ish character of Latin script, code, whitespace or punctuation.</summary>
    private const int LatinCharUnits = 2;

    /// <summary>
    /// One CJK character (Han, kana, Hangul, fullwidth forms). Measured tokenizers spend about 1.6 characters per token
    /// here — two to three times what Latin text costs — so counting a Chinese spec at four characters per token
    /// underestimates it by more than half and lets compaction start too late.
    /// </summary>
    private const int WideCharUnits = 5;

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
        var kilobytes = Math.Max(1, (ContentPlaceholders.DecodedLength(file.Data) + 1023) / 1024);
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
                    TextBlock text => Units(text.Text),
                    ThinkingBlock thinking => Units(thinking.Text),
                    ToolCallBlock call => Units(call.Name) + Units(call.ArgumentsJson),
                    ToolResultBlock result => ToolResultUnits(result),
                    ArtifactRefBlock artifact => Units(artifact.Description) + 32L * LatinCharUnits,
                    ImageBlock => (long)ImageTokens * UnitsPerToken,
                    FileBlock file => FileTokens(file) * UnitsPerToken,
                    _ => 0,
                };
            }
        }

        return Saturate(chars / UnitsPerToken + (long)messages.Count * MessageOverhead);
    }

    /// <summary>A tool result's text plus, for each image in it, the same flat cost an attached image has.</summary>
    private static long ToolResultUnits(ToolResultBlock result)
    {
        if (result.Blocks is [TextBlock only])
        {
            return Units(only.Text);
        }

        long units = 0;
        foreach (var block in result.Blocks)
        {
            units += block switch
            {
                TextBlock text => Units(text.Text),
                ImageBlock => (long)ImageTokens * UnitsPerToken,
                _ => 0,
            };
        }

        return units;
    }

    public int Count(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return Saturate((Units(text) + UnitsPerToken - 1) / UnitsPerToken);
    }

    /// <summary>Estimate units of one string: every character costs by its script, wide characters five and the rest two.</summary>
    private static long Units(string text)
    {
        long units = 0;
        foreach (var character in text)
        {
            units += IsWide(character) ? WideCharUnits : LatinCharUnits;
        }

        return units;
    }

    /// <summary>
    /// Whether one character costs a CJK tokenizer step: Han (any block, including the compatibility ones), kana, Hangul,
    /// CJK radicals and punctuation, and the fullwidth forms. Surrogate pairs (CJK extension B and beyond) are counted as
    /// two ordinary characters — rare enough in a prompt to leave the estimate conservative.
    /// </summary>
    private static bool IsWide(char character) => character switch
    {
        >= '\u1100' and <= '\u115F' => true,
        >= '\u2E80' and <= '\u303E' => true,
        >= '\u3041' and <= '\u33FF' => true,
        >= '\u3400' and <= '\u4DBF' => true,
        >= '\u4E00' and <= '\u9FFF' => true,
        >= '\uA000' and <= '\uA4CF' => true,
        >= '\uAC00' and <= '\uD7A3' => true,
        >= '\uF900' and <= '\uFAFF' => true,
        >= '\uFE30' and <= '\uFE4F' => true,
        >= '\uFF00' and <= '\uFF60' => true,
        >= '\uFFE0' and <= '\uFFE6' => true,
        _ => false,
    };

    private static int Saturate(long value) => (int)Math.Min(int.MaxValue, value);
}
