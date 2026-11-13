using Forge.App.ViewModels;

namespace Forge.App.Tests;

/// <summary>The three-line open of an editor test: a fresh view model with the file at <paramref name="path"/> loaded.</summary>
internal static class TestDocuments
{
    /// <summary>A new <see cref="FileDocumentViewModel"/> that has finished loading <paramref name="path"/>.</summary>
    public static async Task<FileDocumentViewModel> LoadAsync(string path, bool forceText = false)
    {
        var model = FileDocumentViewModel.Create();

        // forceText is for a test whose subject is the editor itself: since W4 an .html/.pdf file opens a placeholder card
        // by default (the file panel never renders them), so a test that measures rendering asks for the source directly.
        await model.LoadAsync(path, CancellationToken.None, forceText: forceText);
        return model;
    }
}
