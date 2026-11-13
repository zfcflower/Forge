using System.Text;

namespace Forge.Abstractions;

/// <summary>
/// What <see cref="ToolResult"/> and <see cref="ToolResultBlock"/> share: the allowed block kinds, the flattened text every
/// text-only consumer reads, and element-wise equality (a record compares a list by reference, which would make two equal
/// results unequal).
/// </summary>
internal static class ToolResultBlocks
{
    /// <summary>Text between two text blocks of one result when they are flattened.</summary>
    private const char TextSeparator = '\n';

    /// <summary>Only text and images can be part of a tool result; anything else is a programming error at the producer.</summary>
    public static IReadOnlyList<ContentBlock> Validate(IReadOnlyList<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        foreach (var block in blocks)
        {
            if (block is not (TextBlock or ImageBlock))
            {
                throw new ArgumentException(
                    $"A tool result holds text and image blocks only, not '{block?.GetType().Name ?? "null"}'.",
                    nameof(blocks));
            }
        }

        return blocks;
    }

    /// <summary>
    /// The text a text-only consumer (a provider without image tool results, search, summaries, the UI's fallback) reads: the
    /// text blocks in order, an image as its <see cref="ContentPlaceholders.ImagePlaceholder"/>, one block per line. A result
    /// of a single text block is that text itself, without a copy.
    /// </summary>
    public static string Flatten(IReadOnlyList<ContentBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        if (blocks.Count == 1 && blocks[0] is TextBlock only)
        {
            return only.Text;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < blocks.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(TextSeparator);
            }

            builder.Append(blocks[index] switch
            {
                TextBlock text => text.Text,
                ImageBlock image => ContentPlaceholders.ImagePlaceholder(image),
                _ => string.Empty,
            });
        }

        return builder.ToString();
    }

    public static bool SequenceEqual(IReadOnlyList<ContentBlock> left, IReadOnlyList<ContentBlock> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!Equals(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    public static int Hash(IReadOnlyList<ContentBlock> blocks)
    {
        var hash = new HashCode();
        foreach (var block in blocks)
        {
            hash.Add(block);
        }

        return hash.ToHashCode();
    }
}
