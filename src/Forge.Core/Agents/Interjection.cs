using System.Text;
using Forge.Abstractions;
using Forge.Core.Storage;

namespace Forge.Core.Agents;

/// <summary>
/// One user message handed to a running turn (<see cref="AgentInstance.Interject(IReadOnlyList{ContentBlock})"/>): typed text
/// and attachments, in the order the user composed them. <see cref="Text"/> is the typed part only (inlined text files are
/// attachments), which is what <see cref="AgentInterjected"/> reports and the UI shows.
/// </summary>
public sealed class Interjection
{
    internal Interjection(IReadOnlyList<ContentBlock> content)
    {
        Content = content;
        Text = TypedText(content);
    }

    /// <summary>The blocks of the message: <see cref="TextBlock"/>, <see cref="ImageBlock"/> and <see cref="FileBlock"/> only.</summary>
    public IReadOnlyList<ContentBlock> Content { get; }

    /// <summary>The typed text (text blocks that are not attached files, joined by a blank line); empty for attachments only.</summary>
    public string Text { get; }

    /// <summary>The attachments (images, files, inlined text files) in their order; empty for a text-only message.</summary>
    public IEnumerable<ContentBlock> Attachments
    {
        get
        {
            foreach (var block in Content)
            {
                if (IsAttachment(block))
                {
                    yield return block;
                }
            }
        }
    }

    internal static Interjection Create(IReadOnlyList<ContentBlock> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var copy = new ContentBlock[content.Count];
        var meaningful = false;
        for (var i = 0; i < copy.Length; i++)
        {
            var block = content[i];
            switch (block)
            {
                case TextBlock text:
                    meaningful |= !string.IsNullOrWhiteSpace(text.Text);
                    break;
                case ImageBlock or FileBlock:
                    meaningful = true;
                    break;
                default:
                    throw new ArgumentException(
                        $"An interjection carries text, images and files only, not {block?.GetType().Name ?? "null"}.",
                        nameof(content));
            }

            copy[i] = block;
        }

        if (!meaningful)
        {
            throw new ArgumentException("An interjection needs text or at least one attachment.", nameof(content));
        }

        return new Interjection(copy);
    }

    /// <summary>
    /// The single user message several pending interjections become: their typed texts joined by a blank line first (one
    /// text block, as a typed turn has), then every attachment in order. Nothing is dropped.
    /// </summary>
    internal static ChatMessage Merge(IReadOnlyList<Interjection> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var content = new List<ContentBlock>();
        var text = new StringBuilder();
        foreach (var interjection in taken)
        {
            if (interjection.Text.Length == 0)
            {
                continue;
            }

            if (text.Length > 0)
            {
                text.Append("\n\n");
            }

            text.Append(interjection.Text);
        }

        if (text.Length > 0)
        {
            content.Add(new TextBlock(text.ToString()));
        }

        foreach (var interjection in taken)
        {
            content.AddRange(interjection.Attachments);
        }

        return new ChatMessage(MessageRole.User, content);
    }

    private static bool IsAttachment(ContentBlock block) =>
        block is ImageBlock or FileBlock || TextAttachment.IsAttachment(block);

    private static string TypedText(IReadOnlyList<ContentBlock> content)
    {
        string? single = null;
        StringBuilder? builder = null;
        foreach (var block in content)
        {
            if (block is not TextBlock text || string.IsNullOrWhiteSpace(text.Text) || TextAttachment.IsAttachment(text))
            {
                continue;
            }

            if (single is null && builder is null)
            {
                single = text.Text;
                continue;
            }

            builder ??= new StringBuilder(single);
            single = null;
            builder.Append("\n\n").Append(text.Text);
        }

        return builder?.ToString() ?? single ?? string.Empty;
    }
}
