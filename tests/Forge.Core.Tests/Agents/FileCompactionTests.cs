using Forge.Abstractions;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>A turn carrying a PDF: it reaches the model, is stored whole, and the summarizer only ever sees a placeholder.</summary>
public sealed class FileCompactionTests
{
    private static readonly string Base64 = Convert.ToBase64String(new byte[6000]);

    [Fact]
    public async Task A_file_turn_reaches_the_model_is_stored_and_is_summarised_as_a_placeholder()
    {
        using var directory = new TempDir();

        await AttachmentCompaction.AssertSummarisedAsPlaceholderAsync(
            directory.Path,
            "files",
            new FileBlock("application/pdf", Base64, "report.pdf"),
            Base64,
            "[file: report.pdf, 6 KB]");
    }
}
