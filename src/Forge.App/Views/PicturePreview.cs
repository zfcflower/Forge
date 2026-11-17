using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.Changes.Markdown;
using Forge.App.Services;
using Forge.App.ViewModels;

namespace Forge.App.Views;

/// <summary>
/// Opens a transcript picture in the file preview tab, together with the other pictures of the same reply (the stretch of the
/// transcript between two user messages) so the preview can step through them. Every picture becomes a file first
/// (<see cref="ChatPicture.Materialize"/>), because the preview opens files.
/// </summary>
internal static class PicturePreview
{
    /// <summary>Opens the picture that <paramref name="source"/> (a thumbnail or a Mermaid block) shows.</summary>
    internal static async Task OpenAsync(Control source)
    {
        if (EditorFileOpener.Handler is not { } open)
        {
            return;
        }

        var pictures = Siblings(source, out var at);
        try
        {
            if (pictures.Count == 0)
            {
                return;
            }

            var files = await Task.Run(() => pictures.Select(item => item.Materialize()).ToList()).ConfigureAwait(true);
            if (files[at] is not { } chosen)
            {
                return;
            }

            PictureSet.Set(files.OfType<string>());
            open(chosen);
        }
        finally
        {
            foreach (var item in pictures)
            {
                item.Release();
            }
        }
    }

    /// <summary>
    /// The pictures of the reply that <paramref name="source"/> sits in (the stretch of the transcript between two user
    /// messages), in reading order, each holding a reference of its own that the caller releases; <paramref name="at"/> is
    /// the one <paramref name="source"/> shows. Only pictures that are on screen are known, since a row the list has recycled
    /// has no view. Outside a transcript list it is just the source's own picture.
    /// </summary>
    private static List<ChatPicture> Siblings(Control source, out int at)
    {
        var result = new List<ChatPicture>();
        at = 0;
        var views = new List<Control>();
        if (source.FindAncestorOfType<ListBox>() is { ItemsSource: System.Collections.IList items } list &&
            source.FindAncestorOfType<ListBoxItem>() is { } own)
        {
            var from = list.IndexFromContainer(own);
            var start = from;
            while (start > 0 && items[start] is not UserMessageViewModel)
            {
                start--;
            }

            var end = from + 1;
            while (end < items.Count && items[end] is not UserMessageViewModel)
            {
                end++;
            }

            foreach (var entry in list.GetRealizedContainers()
                .Select(container => (index: list.IndexFromContainer(container), container))
                .Where(entry => entry.index >= start && entry.index < end)
                .OrderBy(entry => entry.index))
            {
                views.AddRange(entry.container.GetVisualDescendants().OfType<Control>().Where(IsPicture));
            }
        }

        if (!views.Contains(source))
        {
            views.Clear();
            views.Add(source);
        }

        foreach (var view in views)
        {
            var picture = view switch
            {
                MarkdownImageView { Picture: { } tile } when tile.TryRetain() => tile,
                MermaidBlockView { Diagram: { } diagram } => ChatPicture.ForDiagram(diagram, "mermaid"),
                _ => null,
            };
            if (picture is null)
            {
                continue;
            }

            if (ReferenceEquals(view, source))
            {
                at = result.Count;
            }

            result.Add(picture);
        }

        return result;
    }

    private static bool IsPicture(Control control) => control is MarkdownImageView or MermaidBlockView;
}
