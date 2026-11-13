namespace Forge.Abstractions;

/// <summary>
/// The two readings every tool payload needs: the text a transcript, a search index or a provider's text-only shape shows,
/// and the images that ride along with it. Both are computed once when the payload is built, so a hot path that asks twice
/// pays a property read.
/// </summary>
internal static class ToolPayload
{
    /// <summary>
    /// The text of <paramref name="blocks"/>: every <see cref="TextBlock"/> in order, one per line, and a bracketed
    /// placeholder for a block that carries no text (so a text-only reader still knows an image arrived). A payload of
    /// exactly one text block — the shape of every text-only tool — returns that string unchanged, copying nothing.
    /// </summary>
    public static string Text(IReadOnlyList<ContentBlock> blocks)
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

        var builder = new System.Text.StringBuilder(256);
        for (var index = 0; index < blocks.Count; index++)
        {
            var text = blocks[index] switch
            {
                TextBlock content => content.Text,
                ContentBlock other => Describe(other),
            };

            if (text.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(text);
        }

        return builder.ToString();
    }

    /// <summary>The images of <paramref name="blocks"/>, in order; an empty list when there is none.</summary>
    public static IReadOnlyList<ImageBlock> Images(IReadOnlyList<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        var count = 0;
        for (var index = 0; index < blocks.Count; index++)
        {
            if (blocks[index] is ImageBlock)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return [];
        }

        var images = new ImageBlock[count];
        var next = 0;
        for (var index = 0; index < blocks.Count; index++)
        {
            if (blocks[index] is ImageBlock image)
            {
                images[next++] = image;
            }
        }

        return images;
    }

    /// <summary>What a text-only reader says about a block that carries no text.</summary>
    private static string Describe(ContentBlock block) => block switch
    {
        ImageBlock image => "[image: " + image.MediaType + "]",
        FileBlock file => "[file: " + (string.IsNullOrWhiteSpace(file.Name) ? file.MediaType : file.Name) + "]",
        ArtifactRefBlock artifact => "[artifact " + artifact.ArtifactId + "] " + artifact.Description,
        _ => string.Empty,
    };
}
