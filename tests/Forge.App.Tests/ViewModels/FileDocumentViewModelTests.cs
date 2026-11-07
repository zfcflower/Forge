using System.Text;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

public sealed class FileDocumentViewModelTests
{
    [Fact]
    public async Task Csharp_load_exposes_the_exact_file_text_in_the_native_editor_document_without_character_loss()
    {
        const string text = "public class Example {\r\n\tstring Name = \"Forge\"; // \u4e2d\u6587 \U0001F600\r\n}";
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cs");
        await File.WriteAllBytesAsync(path, new UTF8Encoding(false).GetBytes(text));
        try
        {
            var document = FileDocumentViewModel.Create();
            await document.LoadAsync(path, CancellationToken.None);

            Assert.False(document.HasError, document.Error);
            Assert.True(document.HasDocument);
            Assert.Equal(text, document.EditorDocument!.Snapshot.GetText());
            Assert.Equal(text, document.EditText);
            Assert.Equal("public class Example {", document.GetLinesText(1, 1).TrimEnd('\r', '\n'));
            Assert.False(document.IsDirty);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
