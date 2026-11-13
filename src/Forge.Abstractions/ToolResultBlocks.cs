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

    /// <summary>
    /// The result's own words — the text blocks in order, one per line, and nothing standing in for an image. A provider
    /// that writes the images itself (Anthropic's <c>tool_result</c> content array, the Responses API's output array) uses
    /// this instead of <see cref="Flatten"/>: the flattened text already names the image, and sending both the placeholder
    /// and the picture would tell the model about the same screenshot twice.
    /// </summary>
    public static string TextOnly(IReadOnlyList<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (blocks.Count == 0)
        {
            return string.Empty;
        }

        if (blocks.Count == 1 && blocks[0] is TextBlock only)
        {
            return only.Text;
        }

        var builder = new StringBuilder();
        var written = false;
        for (var index = 0; index < blocks.Count; index++)
        {
            if (blocks[index] is not TextBlock text)
            {
                continue;
            }

            if (written)
            {
                builder.Append(TextSeparator);
            }

            builder.Append(text.Text);
            written = true;
        }

        return builder.ToString();
    }

    /// <summary>Whether the result carries a picture, without materializing a list of them.</summary>
    public static bool HasImage(IReadOnlyList<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        for (var index = 0; index < blocks.Count; index++)
        {
            if (blocks[index] is ImageBlock)
            {
                return true;
            }
        }

        return false;
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
