using Forge.Abstractions;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>A turn carrying an image: it reaches the model, is stored whole, and the summarizer only ever sees a placeholder.</summary>
public sealed class ImageCompactionTests
{
    private static readonly string Base64 = Convert.ToBase64String(new byte[6000]);

    [Fact]
    public async Task An_image_turn_reaches_the_model_is_stored_and_is_summarised_as_a_placeholder()
    {
        using var directory = new TempDir();

        await AttachmentCompaction.AssertSummarisedAsPlaceholderAsync(
            directory.Path,
            "images",
            new ImageBlock("image/png", Base64),
            Base64,
            "[image: image/png, 6 KB]");
    }
}
