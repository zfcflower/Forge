using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Forge.App.Services;
using Forge.App.Views;

namespace Forge.App.Changes.Markdown;

/// <summary>
/// One picture in a reply (<c>![alt](path or address)</c>, or an inline <c>svg</c> block) as a fixed-size thumbnail tile.
/// Tiles of one paragraph sit side by side in a wrapping row (see <see cref="MarkdownRenderer"/>); a click opens the viewer
/// over every picture of that row, so the large view is a carousel. Local files and http(s) addresses are loaded off the UI
/// thread through <see cref="ChatPictures"/> (bounded, cached); an SVG stays vector. A picture that cannot be loaded shows its
/// alt text and, on click, opens the link or file with the system as before.
/// </summary>
internal sealed class MarkdownImageView : Border
{
    internal const double TileWidth = 168;
    internal const double TileHeight = 126;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly string url;
    private readonly string? inlineSvg;
    private readonly List<MarkdownImageView> group;
    private readonly Image image;
    private readonly Viewbox vectorBox;
    private readonly MermaidDiagramView vectorView;
    private readonly TextBlock fallback;
    private bool started;
    private int generation;

    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>The loaded picture, or null while loading or when it failed.</summary>
    internal ChatPicture? Picture { get; private set; }

    internal MarkdownImageView(string url, string alt, List<MarkdownImageView> group, string? inlineSvg = null)
    {
        this.url = url;
        this.group = group;
        this.inlineSvg = inlineSvg;
        group.Add(this);
        Width = TileWidth;
        Height = TileHeight;
        HorizontalAlignment = HorizontalAlignment.Left;
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        ClipToBounds = true;
        Margin = new Thickness(0, 0, 8, 8);
        this.Bind(BackgroundProperty, this.GetResourceObservable("SurfaceSubtleBrush"));
        this.Bind(BorderBrushProperty, this.GetResourceObservable("BorderBrush"));

        image = new Image { Stretch = Stretch.Uniform, IsVisible = false, Margin = new Thickness(2) };
        vectorView = new MermaidDiagramView();
        vectorBox = new Viewbox { Stretch = Stretch.Uniform, Child = vectorView, IsVisible = false, Margin = new Thickness(6) };
        fallback = new TextBlock
        {
            Text = "▣ " + (alt.Length > 0 ? alt : url),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        fallback.Bind(TextBlock.ForegroundProperty, fallback.GetResourceObservable("TextSecondaryBrush"));
        Child = new Panel { Children = { image, vectorBox, fallback } };
        Cursor = HandCursor;
        ToolTip.SetTip(this, alt.Length > 0 ? alt + "\n" + Shorten(url) : Shorten(url));
        PointerReleased += OnReleased;
    }

    private static string Shorten(string value) => value.Length > 200 ? value[..200] + "…" : value;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (started)
        {
            return;
        }

        started = true;
        _ = LoadAsync(++generation);
    }

    /// <summary>
    /// A tile that leaves the screen (its reply was rebuilt, closed, or scrolled out of a realized list) gives its picture back:
    /// the bitmap leaves the control and its reference is released, so a picture nobody shows is freed with its last user
    /// instead of lingering until the garbage collector runs. Attaching again reloads it (from the cache when it is still there).
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        generation++;
        started = false;
        var picture = Picture;
        Picture = null;
        image.Source = null;
        image.IsVisible = false;
        vectorView.SetPicture(null);
        vectorBox.IsVisible = false;
        fallback.IsVisible = true;
        picture?.Release();
    }

    private async Task LoadAsync(int ticket)
    {
        ChatPicture? picture;
        if (inlineSvg is { } svg)
        {
            // Not cached (the document is the block's own text): this tile holds the only reference.
            picture = await Task.Run(() => ChatPictures.LoadSvg(svg, url, null)).ConfigureAwait(true);
        }
        else
        {
            picture = await ChatPictures.AcquireAsync(url, LinkLauncher.WorkDirOf(this)).ConfigureAwait(true);
        }

        if (picture is null)
        {
            return;
        }

        if (ticket != generation)
        {
            // Detached (or re-attached and restarted) while loading: this load's reference has no one to show it.
            picture.Release();
            return;
        }

        Picture = picture;
        if (picture.Bitmap is { } bitmap)
        {
            image.Source = bitmap;
            image.IsVisible = true;
        }
        else
        {
            vectorView.SetPicture(picture.Vector);
            vectorBox.IsVisible = true;
        }

        fallback.IsVisible = false;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (Picture is null)
        {
            if (inlineSvg is null && LinkLauncher.LooksOpenable(url))
            {
                _ = LinkLauncher.OpenAsync(this, url);
            }

            return;
        }

        var pictures = new List<ChatPicture>(group.Count);
        var at = 0;
        foreach (var tile in group)
        {
            if (tile.Picture is not { } loaded)
            {
                continue;
            }

            if (ReferenceEquals(tile, this))
            {
                at = pictures.Count;
            }

            pictures.Add(loaded);
        }

        ImageViewerWindow.Open(this, pictures, at);
    }
}
