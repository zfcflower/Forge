using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Forge.App.Services;
using Forge.App.ViewModels.Preview;
using Forge.App.Views;

namespace Forge.App.Changes.Markdown;

/// <summary>
/// A picture in a reply (<c>![alt](path)</c>). Only local files are drawn: a remote address is never fetched on its own
/// (the reply would phone out just by being shown) and stays a link. The bitmap is read and decoded off the UI thread, bounded
/// to <see cref="MaxEdge"/>, and cached by path so a streaming paragraph that is re-rendered every flush does not decode the
/// same file again. A click opens the file in the system's viewer; a file that cannot be read shows its alt text instead.
/// </summary>
internal sealed class MarkdownImageView : Border
{
    private const int MaxEdge = 1280;
    private const long MaxBytes = 32L * 1024 * 1024;
    private const int CacheLimit = 24;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    /// <summary>Decoded pictures by path, stamped with the file's write time and length so an edited file reloads. UI thread only.</summary>
    private static readonly Dictionary<string, (long Stamp, Bitmap Bitmap)> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Queue<string> CacheOrder = new();

    private readonly string url;
    private readonly string alt;
    private readonly Image image;
    private readonly TextBlock fallback;
    private string? resolved;
    private bool started;

    protected override Type StyleKeyOverride => typeof(Border);

    internal MarkdownImageView(string url, string alt)
    {
        this.url = url;
        this.alt = alt;
        HorizontalAlignment = HorizontalAlignment.Left;
        CornerRadius = new CornerRadius(6);
        ClipToBounds = true;
        image = new Image { Stretch = Stretch.Uniform, MaxHeight = 480, IsVisible = false, StretchDirection = StretchDirection.DownOnly };
        fallback = new TextBlock { Text = "▣ " + (alt.Length > 0 ? alt : url), TextWrapping = TextWrapping.Wrap };
        fallback.Bind(TextBlock.ForegroundProperty, fallback.GetResourceObservable("TextSecondaryBrush"));
        Child = new Panel { Children = { image, fallback } };
        Cursor = HandCursor;
        ToolTip.SetTip(this, alt.Length > 0 ? alt + "\n" + url : url);
        PointerReleased += OnReleased;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (started)
        {
            return;
        }

        started = true;
        var target = LinkTarget.Classify(url, LinkLauncher.WorkDirOf(this));
        if (target.Kind is not (LinkTargetKind.SystemFile or LinkTargetKind.EditorFile))
        {
            // A remote address (or an unresolvable path) stays text; a web picture is a link, opened on click.
            Cursor = LinkLauncher.LooksOpenable(url) ? HandCursor : null;
            return;
        }

        resolved = target.Location;
        _ = LoadAsync(target.Location);
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (resolved is { } path)
        {
            _ = LinkLauncher.OpenWithSystemAsync(this, path);
        }
        else if (LinkLauncher.LooksOpenable(url))
        {
            _ = LinkLauncher.OpenAsync(this, url);
        }
    }

    private async Task LoadAsync(string path)
    {
        Bitmap? bitmap = null;
        try
        {
            var info = new FileInfo(path);
            var stamp = info.LastWriteTimeUtc.Ticks ^ info.Length;
            if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp)
            {
                bitmap = cached.Bitmap;
            }
            else if (info.Length is > 0 and <= MaxBytes)
            {
                bitmap = await Task.Run(() =>
                {
                    try
                    {
                        return ImageHeader.DecodeBounded(File.ReadAllBytes(path), MaxEdge, BitmapInterpolationMode.MediumQuality);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                        or NotSupportedException or InvalidOperationException)
                    {
                        return null;
                    }
                }).ConfigureAwait(true);
                if (bitmap is not null)
                {
                    Remember(path, stamp, bitmap);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            bitmap = null;
        }

        if (bitmap is null)
        {
            return;
        }

        image.Source = bitmap;
        image.IsVisible = true;
        fallback.IsVisible = false;
    }

    private static void Remember(string path, long stamp, Bitmap bitmap)
    {
        if (!Cache.ContainsKey(path))
        {
            CacheOrder.Enqueue(path);
            while (CacheOrder.Count > CacheLimit)
            {
                // The evicted bitmap may still be on screen in another reply, so it is left to the garbage collector.
                Cache.Remove(CacheOrder.Dequeue());
            }
        }

        Cache[path] = (stamp, bitmap);
    }
}
