using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Forge.App.Localization;
using Forge.App.Services;

namespace Forge.App.Views;

/// <summary>
/// The large view of the transcript's pictures: a carousel over the pictures of one reply (previous/next, arrow keys), zoom
/// with the wheel or the buttons, drag to pan, fit / actual size, and "show in folder" for a picture that is a local file.
/// A single diagram (Mermaid or SVG) opens the same window with one entry. Esc closes it.
/// </summary>
internal sealed class ImageViewerWindow : Window
{
    private readonly IReadOnlyList<ChatPicture> pictures;
    private readonly ZoomPanView view = new();
    private readonly TextBlock caption = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock zoomText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 48, TextAlignment = TextAlignment.Center };
    private readonly Button previous;
    private readonly Button next;
    private readonly Button reveal;
    private int index;

    private ImageViewerWindow(IReadOnlyList<ChatPicture> pictures, int index)
    {
        this.pictures = pictures;
        this.index = index;
        Title = Loc.Instance[ViewerStrings.Title];
        Width = 1000;
        Height = 720;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#202124"));
        KeyboardNavigationProperties();

        previous = Tool("‹", ViewerStrings.Previous, () => Go(-1));
        next = Tool("›", ViewerStrings.Next, () => Go(1));
        reveal = Tool("⌂", ViewerStrings.Reveal, Reveal);
        var bar = new DockPanel { Margin = new Thickness(8, 6), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        buttons.Children.Add(previous);
        buttons.Children.Add(next);
        buttons.Children.Add(Tool("−", ViewerStrings.ZoomOut, () => view.ZoomBy(1 / 1.25)));
        buttons.Children.Add(zoomText);
        buttons.Children.Add(Tool("+", ViewerStrings.ZoomIn, () => view.ZoomBy(1.25)));
        buttons.Children.Add(Tool("⤢", ViewerStrings.Fit, view.Fit));
        buttons.Children.Add(Tool("1:1", ViewerStrings.Actual, view.Actual));
        buttons.Children.Add(reveal);
        DockPanel.SetDock(buttons, Dock.Left);
        caption.Margin = new Thickness(12, 0, 0, 0);
        bar.Children.Add(buttons);
        bar.Children.Add(caption);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(view);
        Content = root;
        zoomText.Foreground = Brushes.White;
        caption.Foreground = new SolidColorBrush(Color.Parse("#B0B4BA"));
        view.ZoomChanged += () => zoomText.Text = (view.Zoom * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
        Show(this.index);
    }

    /// <summary>
    /// Opens the viewer on <paramref name="pictures"/>[<paramref name="index"/>] over <paramref name="owner"/>. The window takes
    /// a reference of its own to every picture (the caller keeps and releases its own) and gives them back when it closes.
    /// </summary>
    internal static void Open(Control owner, IReadOnlyList<ChatPicture> pictures, int index)
    {
        var held = new List<ChatPicture>(pictures.Count);
        var at = 0;
        for (var i = 0; i < pictures.Count; i++)
        {
            if (!pictures[i].TryRetain())
            {
                continue;
            }

            if (i == index)
            {
                at = held.Count;
            }

            held.Add(pictures[i]);
        }

        if (held.Count == 0)
        {
            return;
        }

        var window = new ImageViewerWindow(held, at);
        window.Closed += (_, _) =>
        {
            // Stop drawing before the references go: the view must not touch a disposed bitmap.
            window.view.SetPicture(null);
            foreach (var picture in held)
            {
                picture.Release();
            }
        };
        if (TopLevel.GetTopLevel(owner) is Window parent)
        {
            window.Show(parent);
        }
        else
        {
            window.Show();
        }
    }

    private void KeyboardNavigationProperties()
    {
        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    break;
                case Key.Left or Key.PageUp:
                    Go(-1);
                    break;
                case Key.Right or Key.PageDown:
                    Go(1);
                    break;
                case Key.OemPlus or Key.Add:
                    view.ZoomBy(1.25);
                    break;
                case Key.OemMinus or Key.Subtract:
                    view.ZoomBy(1 / 1.25);
                    break;
                case Key.D0 or Key.NumPad0:
                    view.Fit();
                    break;
                case Key.D1 or Key.NumPad1:
                    view.Actual();
                    break;
                default:
                    return;
            }

            e.Handled = true;
        };
    }

    private static Button Tool(string glyph, string key, Action click)
    {
        var button = new Button
        {
            Content = glyph,
            MinWidth = 32,
            Height = 30,
            Padding = new Thickness(8, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.Parse("#3A3D41")),
        };
        ToolTip.SetTip(button, Loc.Instance[key]);
        Avalonia.Automation.AutomationProperties.SetName(button, Loc.Instance[key]);
        button.Click += (_, _) => click();
        return button;
    }

    private void Go(int step)
    {
        if (pictures.Count < 2)
        {
            return;
        }

        index = (index + step + pictures.Count) % pictures.Count;
        Show(index);
    }

    private void Show(int at)
    {
        var picture = pictures[at];
        view.SetPicture(picture);
        previous.IsEnabled = next.IsEnabled = pictures.Count > 1;
        reveal.IsVisible = picture.LocalPath is not null;
        var name = picture.LocalPath is { } path ? Path.GetFileName(path) : picture.Url;
        caption.Text = (pictures.Count > 1 ? (at + 1).ToString(CultureInfo.InvariantCulture) + " / " + pictures.Count.ToString(CultureInfo.InvariantCulture) + "   " : string.Empty) + name;
    }

    private void Reveal()
    {
        if (pictures[index].LocalPath is not { } path || !File.Exists(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = "/select,\"" + path + "\"", UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
