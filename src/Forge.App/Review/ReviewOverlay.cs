using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Forge.App.Localization;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Forge.App.Logging;
using ZLogger;

namespace Forge.App.Review;

internal sealed class ReviewOverlayParts
{
    public required Canvas Layer { get; init; }
    public required Button Icon { get; init; }
    public required Border Composer { get; init; }
    public required TextBlock RangeText { get; init; }
    public required TextBox BodyBox { get; init; }
    public required TextBlock ComposerStatus { get; init; }
    public required Button SaveButton { get; init; }
    public required Button SendButton { get; init; }
    public required Button DeleteButton { get; init; }
    public required Button CancelButton { get; init; }
    public required Button SendAllButton { get; init; }
    public required TextBlock StatusText { get; init; }
}

/// <summary>Anchors the floating composer and review markers to editor viewport coordinates.</summary>
internal sealed class ReviewOverlay : IDisposable
{
    private const double IconSize = 26;
    private const double MarkerSize = 20;
    private const double Gap = 4;
    private const double Edge = 8;
    private const string GlyphData = "M4 5h16v11H9l-4 4v-4H4z";
    private readonly TextEditor editor;
    private readonly TextArea area;
    private readonly TextView textView;
    private readonly ReviewOverlayParts ui;
    private readonly List<Button> markers = [];
    private IReviewSurface? controller;
    private PendingRange? pending;
    private bool pendingFromGutter;
    private IPointer? gutterPointer;
    private TextDocument? gutterDocument;
    private ReviewDraft? active;
    private bool disposed;
    private double markerSize = MarkerSize;

    public ReviewOverlay(TextEditor editor, ReviewOverlayParts parts)
    {
        this.editor = editor;
        ui = parts;
        area = editor.TextArea;
        textView = area.TextView;
        area.SelectionChanged += OnSelectionChanged;
        area.Caret.PositionChanged += OnCaretMoved;
        textView.ScrollOffsetChanged += OnViewChanged;
        textView.VisualLinesChanged += OnViewChanged;
        editor.PropertyChanged += OnEditorPropertyChanged;
        editor.AddHandler(InputElement.KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        area.AddHandler(InputElement.PointerPressedEvent, OnGutterPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        area.AddHandler(InputElement.PointerReleasedEvent, OnGutterReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

        ui.Icon.Click += OnIconClick;
        ui.SaveButton.Click += OnSaveClick;
        ui.SendButton.Click += OnSendClick;
        ui.DeleteButton.Click += OnDeleteClick;
        ui.CancelButton.Click += OnCancelClick;
        ui.SendAllButton.Click += OnSendAllClick;
        ui.BodyBox.TextChanged += OnBodyChanged;
        ui.Composer.AddHandler(InputElement.KeyDownEvent, OnComposerKeyDown);
        ui.Composer.PropertyChanged += OnComposerPropertyChanged;
    }

    public void Bind(IReviewSurface? value)
    {
        if (ReferenceEquals(controller, value))
        {
            Refresh();
            return;
        }

        if (controller is not null)
        {
            controller.Changed -= OnControllerChanged;
            controller.DocumentReset -= OnDocumentReset;
            Loc.Instance.LanguageChanged -= OnLanguageChanged;
        }

        CloseComposer(discard: true);
        ClearPending();
        controller = value;
        if (controller is not null)
        {
            controller.Changed += OnControllerChanged;
            controller.DocumentReset += OnDocumentReset;
            Loc.Instance.LanguageChanged += OnLanguageChanged;
        }

        Refresh();
    }

    public void Refresh()
    {
        if (disposed)
        {
            return;
        }

        RefreshTexts();
        var status = controller?.Status ?? default;
        ui.StatusText.Text = status.IsEmpty ? null : status.Resolve();
        ui.StatusText.IsVisible = !status.IsEmpty;
        ui.SendAllButton.IsVisible = controller is { Drafts.Count: > 0 };
        ui.SendAllButton.IsEnabled = controller is { CanCompose: true, IsBusy: false };
        UpdateComposerState();
        Layout();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Bind(null);
        disposed = true;
        area.SelectionChanged -= OnSelectionChanged;
        area.Caret.PositionChanged -= OnCaretMoved;
        textView.ScrollOffsetChanged -= OnViewChanged;
        textView.VisualLinesChanged -= OnViewChanged;
        editor.PropertyChanged -= OnEditorPropertyChanged;
        editor.RemoveHandler(InputElement.KeyDownEvent, OnEditorKeyDown);
        area.RemoveHandler(InputElement.PointerPressedEvent, OnGutterPressed);
        area.RemoveHandler(InputElement.PointerReleasedEvent, OnGutterReleased);

        ui.Icon.Click -= OnIconClick;
        ui.SaveButton.Click -= OnSaveClick;
        ui.SendButton.Click -= OnSendClick;
        ui.DeleteButton.Click -= OnDeleteClick;
        ui.CancelButton.Click -= OnCancelClick;
        ui.SendAllButton.Click -= OnSendAllClick;
        ui.BodyBox.TextChanged -= OnBodyChanged;
        ui.Composer.RemoveHandler(InputElement.KeyDownEvent, OnComposerKeyDown);
        ui.Composer.PropertyChanged -= OnComposerPropertyChanged;
        foreach (var marker in markers)
        {
            marker.Click -= OnMarkerClick;
        }
    }

    private void OnControllerChanged(object? sender, EventArgs e) => Refresh();

    private void OnDocumentReset(object? sender, EventArgs e)
    {
        CloseComposer(discard: true);
        ClearPending();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
        }
        else
        {
            Dispatcher.UIThread.Post(Refresh);
        }
    }

    private void OnViewChanged(object? sender, EventArgs e) => Layout();

    private void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty)
        {
            Layout();
        }
    }

    private void OnComposerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty && e.OldValue is Rect before && e.NewValue is Rect after && before.Size != after.Size)
        {
            Layout();
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (disposed || controller is null || active is not null)
        {
            return;
        }

        if (!area.Selection.IsEmpty)
        {
            PendingFromSelection();
        }
        else if (!pendingFromGutter)
        {
            ClearPending();
        }
    }

    private void PendingFromSelection()
    {
        var doc = area.Document;
        if (doc is null || area.Selection.IsEmpty)
        {
            return;
        }

        var segment = area.Selection.SurroundingSegment;
        var startOffset = Math.Clamp(segment.Offset, 0, doc.TextLength);
        var endOffset = Math.Clamp(Math.Max(segment.Offset, segment.EndOffset), 0, doc.TextLength);
        var startLine = doc.GetLineByOffset(startOffset).LineNumber;
        var endLine = doc.GetLineByOffset(endOffset);
        var last = endLine.LineNumber;
        if (endOffset == endLine.Offset && last > startLine)
        {
            last--;
        }

        // The icon follows the pointer's end of the selection: the moving end of the selection is where the mouse let go.
        var anchor = area.Selection.EndPosition;
        var anchorOffset = Math.Clamp(doc.GetOffset(anchor.Location), 0, doc.TextLength);
        SetPending(startLine, last, fromGutter: false, anchorOffset);
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        if (pendingFromGutter && pending is { } range && area.Selection.IsEmpty && area.Caret.Line != range.Start)
        {
            ClearPending();
        }
    }

    private void OnGutterPressed(object? sender, PointerPressedEventArgs e)
    {
        gutterPointer = null;
        if (disposed || controller is null || !e.GetCurrentPoint(area).Properties.IsLeftButtonPressed)
        {
            return;
        }
        var point = e.GetPosition(area);
        foreach (var margin in area.LeftMargins)
        {
            if (margin is (LineNumberMargin or IReviewGutterMargin) && margin.IsVisible
                && margin.TranslatePoint(default, area) is { } origin
                && new Rect(origin, margin.Bounds.Size).Contains(point))
            {
                gutterPointer = e.Pointer;
                gutterDocument = area.Document;
                return;
            }
        }
    }

    private void OnGutterReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pointer = gutterPointer;
        gutterPointer = null;
        if (disposed || controller is null || pointer is null || !ReferenceEquals(pointer, e.Pointer)
            || e.InitialPressMouseButton != MouseButton.Left || !ReferenceEquals(gutterDocument, area.Document))
        {
            return;
        }

        var y = e.GetPosition(textView).Y;
        Dispatcher.UIThread.Post(() => HandleGutterClick(y), DispatcherPriority.Input);
    }

    private void HandleGutterClick(double y)
    {
        if (disposed || active is not null)
        {
            return;
        }

        if (!area.Selection.IsEmpty)
        {
            PendingFromSelection();
            return;
        }

        if (textView.GetDocumentLineByVisualTop(y + textView.ScrollOffset.Y) is { } line)
        {
            SetPending(line.LineNumber, line.LineNumber, fromGutter: true, line.Offset);
        }
    }

    /// <param name="anchorOffset">Document offset the floating icon hangs off; the icon sits at its bottom right.</param>
    private void SetPending(int start, int end, bool fromGutter, int anchorOffset)
    {
        if (controller is { } surface && !surface.CanCommentOn(start, end))
        {
            ClearPending();
            return;
        }

        pending = new PendingRange(start, end, anchorOffset);
        pendingFromGutter = fromGutter;
        Layout();
    }

    private void ClearPending()
    {
        pending = null;
        pendingFromGutter = false;
        Layout();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        if (active is not null)
        {
            if (controller is { IsBusy: true })
            {
                e.Handled = true;
                return;
            }
            CloseComposer(discard: true);
            area.Focus();
            e.Handled = true;
        }
        else if (pending is not null)
        {
            ClearPending();
        }
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && active is not null)
        {
            if (controller is { IsBusy: true })
            {
                e.Handled = true;
                return;
            }
            CloseComposer(discard: true);
            area.Focus();
            e.Handled = true;
        }
    }

    private void OnIconClick(object? sender, RoutedEventArgs e)
    {
        if (pending is { } range && controller?.BeginDraft(range.Start, range.End) is { } draft)
        {
            OpenComposer(draft);
        }
    }

    private void OnMarkerClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ReviewDraft draft })
        {
            OpenComposer(draft);
        }
    }

    private void OpenComposer(ReviewDraft draft)
    {
        CloseComposer(discard: true);
        active = draft;
        ui.BodyBox.Text = draft.Body;
        ui.Composer.IsVisible = true;
        UpdateComposerState();
        Layout();
        Dispatcher.UIThread.Post(() => ui.BodyBox.Focus());
    }

    private void CloseComposer(bool discard)
    {
        if (active is { Id: 0 } fresh && discard)
        {
            controller?.Discard(fresh);
        }

        active = null;
        ui.Composer.IsVisible = false;
        Layout();
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) =>
        Act(draft => controller!.SaveAsync(draft, ui.BodyBox.Text ?? string.Empty));
    private void OnSendClick(object? sender, RoutedEventArgs e) =>
        Act(draft => controller!.SendAsync(draft, ui.BodyBox.Text ?? string.Empty));
    private void OnDeleteClick(object? sender, RoutedEventArgs e) => Act(draft => controller!.DeleteAsync(draft));

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (controller is { IsBusy: true })
        {
            return;
        }
        CloseComposer(discard: true);
        area.Focus();
    }

    private async void OnSendAllClick(object? sender, RoutedEventArgs e)
    {
        if (controller is null)
        {
            return;
        }

        try
        {
            await controller.SendFileAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    private async void Act(Func<ReviewDraft, Task<bool>> work)
    {
        if (active is not { } draft || controller is null)
        {
            return;
        }

        try
        {
            if (await work(draft).ConfigureAwait(true) && ReferenceEquals(active, draft))
            {
                CloseComposer(discard: false);
                area.Focus();
            }
        }
        catch (Exception exception)
        {
            Report(exception);
        }
    }

    private void Report(Exception exception)
    {
        AppLogs.Review.ZLogError(exception, $"ui action failed");
        ui.StatusText.Text = exception.Message;
        ui.StatusText.IsVisible = true;
    }

    private void OnBodyChanged(object? sender, TextChangedEventArgs e) => UpdateComposerState();

    private void RefreshTexts()
    {
        var loc = Loc.Instance;
        ui.SaveButton.Content = loc[ReviewStrings.Save];
        ui.SendButton.Content = loc[ReviewStrings.Send];
        ui.CancelButton.Content = loc[ReviewStrings.Cancel];
        ui.DeleteButton.Content = loc[ReviewStrings.Delete];
        ui.BodyBox.PlaceholderText = loc[ReviewStrings.Placeholder];
        ToolTip.SetTip(ui.Icon, loc[ReviewStrings.AddComment]);
        AutomationProperties.SetName(ui.Icon, loc[ReviewStrings.AddComment]);
        var send = loc.Format(ReviewStrings.SendFile, TextFormat.Number(controller?.Drafts.Count ?? 0));
        ToolTip.SetTip(ui.SendAllButton, send);
        AutomationProperties.SetName(ui.SendAllButton, send);
    }

    private void UpdateComposerState()
    {
        if (active is not { } draft || controller?.Document is null)
        {
            return;
        }

        var lost = controller.IsLost(draft);
        var usable = controller is { CanCompose: true, IsBusy: false } && !lost && !draft.IsSent
            && !string.IsNullOrWhiteSpace(ui.BodyBox.Text);
        ui.SaveButton.IsEnabled = usable;
        ui.SendButton.IsEnabled = usable;
        ui.DeleteButton.IsVisible = draft.Id != 0;
        ui.DeleteButton.IsEnabled = controller is { CanCompose: true, IsBusy: false };
        ui.CancelButton.IsEnabled = !controller.IsBusy;
        var status = lost ? Loc.Instance[ReviewStrings.Lost] : controller.Status.Resolve();
        ui.ComposerStatus.Text = status;
        ui.ComposerStatus.IsVisible = !string.IsNullOrEmpty(status);
        var (start, end) = draft.TryGetLines(out var s, out var e) ? (s, e) : (draft.StoredStart, draft.StoredEnd);
        ui.RangeText.Text = InspectorStrings.ReviewLine(start, end);
    }

    private Rect? Viewport()
    {
        if (textView.TranslatePoint(default, ui.Layer) is not { } origin)
        {
            return null;
        }

        var rect = new Rect(origin, textView.Bounds.Size);
        return rect.Width >= 120 && rect.Height >= 60 ? rect : null;
    }

    private (double Top, double Bottom) LineSpan(Rect viewport, TextDocument doc, int line)
    {
        line = Math.Clamp(line, 1, Math.Max(1, doc.LineCount));
        var top = textView.GetVisualTopByDocumentLine(line);
        var bottom = line < doc.LineCount ? textView.GetVisualTopByDocumentLine(line + 1) : textView.DocumentHeight;
        if (bottom <= top)
        {
            bottom = top + textView.DefaultLineHeight;
        }

        var shift = viewport.Y - textView.ScrollOffset.Y;
        return (top + shift, bottom + shift);
    }

    private void Layout()
    {
        if (disposed)
        {
            return;
        }

        var doc = controller?.Document;
        var ready = controller is { CanCompose: true } && doc is not null && ReferenceEquals(area.Document, doc);
        if (!ready || Viewport() is not { } viewport || doc is null)
        {
            ui.Icon.IsVisible = false;
            foreach (var marker in markers)
            {
                marker.IsVisible = false;
            }

            if (active is null)
            {
                ui.Composer.IsVisible = false;
            }

            return;
        }

        markerSize = Math.Clamp(textView.DefaultLineHeight - 2, 12, MarkerSize);
        PlaceIcon(viewport, doc);
        PlaceMarkers(viewport, doc);
        PlaceComposer(viewport, doc);
    }

    private void PlaceIcon(Rect viewport, TextDocument doc)
    {
        if (active is not null || pending is not { } range)
        {
            ui.Icon.IsVisible = false;
            return;
        }

        // Bottom right of the anchor: just below the line's last visual line, just right of the offset. The icon used to
        // hug the right edge of the viewport, far away from the text the user had just selected.
        var offset = Math.Clamp(range.AnchorOffset, 0, doc.TextLength);
        var bottom = textView.GetVisualPosition(new TextViewPosition(doc.GetLocation(offset)), VisualYPosition.LineBottom);
        var anchor = new Point(
            viewport.X + bottom.X - textView.ScrollOffset.X,
            viewport.Y + bottom.Y - textView.ScrollOffset.Y);
        var placed = ReviewIconPlacement.Find(viewport, anchor);
        Canvas.SetLeft(ui.Icon, placed.X);
        Canvas.SetTop(ui.Icon, placed.Y);
        ui.Icon.IsVisible = true;
    }

    private void PlaceMarkers(Rect viewport, TextDocument doc)
    {
        var shown = 0;
        var perLine = new Dictionary<int, int>();
        foreach (var draft in controller!.Drafts)
        {
            var line = draft.TryGetLines(out var start, out _) ? start : draft.StoredStart;
            var span = LineSpan(viewport, doc, line);
            if (span.Bottom < viewport.Top || span.Top > viewport.Bottom)
            {
                continue;
            }

            if (shown == markers.Count)
            {
                markers.Add(CreateMarker());
            }

            perLine.TryGetValue(line, out var stacked);
            perLine[line] = stacked + 1;
            var marker = markers[shown++];
            marker.Tag = draft;
            marker.Classes.Set("lost", controller.IsLost(draft));
            ToolTip.SetTip(marker, FirstLine(draft.Body));
            marker.Width = markerSize;
            marker.Height = markerSize;
            Canvas.SetLeft(marker, Math.Max(viewport.X, viewport.Right - Edge - markerSize - stacked * (markerSize + Gap)));
            Canvas.SetTop(marker, Math.Clamp(span.Top + 1, viewport.Top, viewport.Bottom - markerSize));
            marker.IsVisible = true;
        }

        for (var i = shown; i < markers.Count; i++)
        {
            markers[i].IsVisible = false;
            markers[i].Tag = null;
        }
    }

    private void PlaceComposer(Rect viewport, TextDocument doc)
    {
        if (active is not { } draft)
        {
            return;
        }

        var width = Math.Min(Math.Clamp(viewport.Width - 2 * Edge - 40, 220, 560), viewport.Width - 2 * Edge);
        ui.Composer.Width = width;
        ui.Composer.MaxHeight = Math.Max(120, viewport.Height - 2 * Gap);
        ui.Composer.IsVisible = true;
        ui.Composer.Measure(new Size(width, ui.Composer.MaxHeight));
        var height = ui.Composer.DesiredSize.Height;
        var (start, end) = draft.TryGetLines(out var s, out var e) ? (s, e) : (draft.StoredStart, draft.StoredEnd);
        var first = LineSpan(viewport, doc, start);
        var last = LineSpan(viewport, doc, end);
        var minY = viewport.Top + Gap;
        var maxY = Math.Max(minY, viewport.Bottom - Gap - height);
        var y = last.Bottom + Gap;
        if (y > maxY)
        {
            var above = first.Top - Gap - height;
            y = above >= minY ? above : maxY;
        }

        var x = Math.Max(viewport.X + Edge, Math.Min(viewport.X + Edge + 40, viewport.Right - Edge - width));
        Canvas.SetLeft(ui.Composer, x);
        Canvas.SetTop(ui.Composer, Math.Clamp(y, minY, maxY));
    }

    private Button CreateMarker()
    {
        var glyph = new ShapePath { Data = Geometry.Parse(GlyphData), Width = 9, Height = 9, Stretch = Stretch.Uniform };
        glyph.Classes.Add("reviewGlyph");
        var marker = new Button { Width = MarkerSize, Height = MarkerSize, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), Content = glyph, IsVisible = false };
        marker.Classes.Add("reviewMarker");
        marker.Click += OnMarkerClick;
        ui.Layer.Children.Add(marker);
        return marker;
    }

    private static string FirstLine(string body)
    {
        var end = body.IndexOfAny(['\r', '\n']);
        return end < 0 ? body : body[..end];
    }

    /// <summary>A pending review range and the document offset the floating icon hangs off (its bottom right).</summary>
    private readonly record struct PendingRange(int Start, int End, int AnchorOffset);
}
