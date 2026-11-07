using Forge.App.ViewModels;
using Forge.Editor.Highlighting;
using Forge.Editor.TextMate;
using Xunit;

namespace Forge.App.Tests.ViewModels;

public sealed class FileDocumentViewModelTests
{
    [Fact]
    public async Task Csharp_preview_exposes_highlight_categories_for_rendering()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cs");
        await File.WriteAllTextAsync(path, "public class Example { string Name = \"Forge\"; }");
        try
        {
            var document = FileDocumentViewModel.Create(new GrammarRegistry());
            await document.LoadAsync(path, CancellationToken.None);

            var categories = document.LineItems.SelectMany(static line => line.Segments)
                .Select(static segment => segment.Category)
                .ToArray();

            Assert.Contains(TokenCategory.Keyword, categories);
            Assert.Contains(TokenCategory.String, categories);
            Assert.Equal(document.LineItems[0].Text, string.Concat(document.LineItems[0].Segments.Select(static segment => segment.Text)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
