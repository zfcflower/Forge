using Forge.App.ViewModels;

namespace Forge.App.Tests;

/// <summary>The three-line open of an editor test: a fresh view model with the file at <paramref name="path"/> loaded.</summary>
internal static class TestDocuments
{
    /// <summary>A new <see cref="FileDocumentViewModel"/> that has finished loading <paramref name="path"/>.</summary>
    public static async Task<FileDocumentViewModel> LoadAsync(string path)
    {
        var model = FileDocumentViewModel.Create();
        await model.LoadAsync(path, CancellationToken.None);
        return model;
    }
}
