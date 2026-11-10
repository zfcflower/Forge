using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Forge.App.Editing;
using Forge.App.Localization;
using Forge.App.Review;
using Forge.Workspace;

namespace Forge.App.Changes;

/// <summary>
/// The review diff: every changed file in one read-only AvaloniaEdit document (two in split mode). File headers, block bars,
/// fold bars and Markdown previews are placeholder lines that only reserve height; the real controls live in
/// <c>OverlayLayer</c> and exist for the visible rows only.
/// </summary>
internal sealed partial class DiffEditorView : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<IDiffFileItem>?> FilesProperty =
        AvaloniaProperty.Register<DiffEditorView, IReadOnlyList<IDiffFileItem>?>(nameof(Files));

    public static readonly StyledProperty<DiffViewMode> ModeProperty =
        AvaloniaProperty.Register<DiffEditorView, DiffViewMode>(nameof(Mode));

    public static readonly StyledProperty<bool> WordWrapProperty =
        AvaloniaProperty.Register<DiffEditorView, bool>(nameof(WordWrap));

    public static readonly StyledProperty<DiffReviewContext?> ReviewProperty =
        AvaloniaProperty.Register<DiffEditorView, DiffReviewContext?>(nameof(Review));

    public static readonly StyledProperty<Func<IDiffFileItem, Control>?> MarkdownPreviewFactoryProperty =
        AvaloniaProperty.Register<DiffEditorView, Func<IDiffFileItem, Control>?>(nameof(MarkdownPreviewFactory));

    private readonly DiffPalette palette = new();
    private readonly Side primary;
    private readonly Side secondary;
    private readonly DiffReviewController reviewController = new();
    private readonly Dictionary<IDiffFileItem, int> indexOf = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> dirty = [];
    private readonly HashSet<int> primeFiles = [];
    private readonly Dictionary<IDiffFileItem, HashSet<int>> expanded = [];
    private readonly Dictionary<IDiffFileItem, Control> embeds = [];
    private readonly Dictionary<(DiffRowKind Kind, int File, int Key), Control> active = [];
    private readonly Stack<DiffFileHeader> headerPool = [];
    private readonly Stack<DiffBlockBar> barPool = [];
    private readonly Stack<DiffFoldBar> foldPool = [];
    private IReadOnlyList<IDiffFileItem> items = [];
    private double[] embedHeights = [];
    private ReviewOverlay? overlay;
    private TextEditor? overlayEditor;
    private FileDiff[] diffs = [];
    private bool flushPending;
    private bool fullPending;
    private bool primeAll = true;
    private bool subscribed;
    private bool syncing;
    private bool attached;
    private int lastTopFile = -1;
    private int embedPasses;
    private int calibrations;
    private bool building;
    private IDiffFileItem? pendingScroll;
    private Anchor? pendingAnchor;
    private bool split;

    public DiffEditorView()
    {
        InitializeComponent();
        primary = new Side(PrimaryEditor, palette, DiffGutterSide.Inline);
        secondary = new Side(SecondaryEditor, palette, DiffGutterSide.New);
        foreach (var side in new[] { primary, secondary })
        {
            side.Editor.AddHandler(InputElement.KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
            side.Editor.TextArea.TextView.ScrollOffsetChanged += OnScrollChanged;
            side.Editor.TextArea.TextView.VisualLinesChanged += OnVisualLinesChanged;
        }

        OverlayLayer.AddHandler(InputElement.PointerWheelChangedEvent, OnOverlayWheel);
        SizeChanged += (_, _) => UpdateOverlay();
        ApplyLayout();
    }

    /// <summary>Raised when the file under the top edge of the viewport changes.</summary>
    public event Action<IDiffFileItem>? TopFileChanged;

    public IReadOnlyList<IDiffFileItem>? Files
    {
        get => GetValue(FilesProperty);
        set => SetValue(FilesProperty, value);
    }

    public DiffViewMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public bool WordWrap
    {
        get => GetValue(WordWrapProperty);
        set => SetValue(WordWrapProperty, value);
    }

    public DiffReviewContext? Review
    {
        get => GetValue(ReviewProperty);
        set => SetValue(ReviewProperty, value);
    }

    public Func<IDiffFileItem, Control>? MarkdownPreviewFactory
    {
        get => GetValue(MarkdownPreviewFactoryProperty);
        set => SetValue(MarkdownPreviewFactoryProperty, value);
    }

    /// <summary>Row map of the left (inline: only) document.</summary>
    internal DiffRowMap PrimaryMap => primary.Map;

    /// <summary>Row map of the right (new) document in split mode.</summary>
    internal DiffRowMap SecondaryMap => secondary.Map;

    internal DiffReviewController ReviewController => reviewController;

    internal int ActiveOverlayCount => active.Count;

    internal IReadOnlyCollection<Control> ActiveOverlayControls => active.Values;

    internal bool IsSplit => split;

    /// <summary>How many per-file row sets the builder produced since creation; lets tests prove a change touched one file only.</summary>
    internal int FilesBuilt { get; private set; }

    internal TextEditor ReviewEditor => split ? SecondaryEditor : PrimaryEditor;

    /// <summary>Scrolls so that the header of <paramref name="file"/> is at the top.</summary>
    public void ScrollToFile(IDiffFileItem file)
    {
        var index = IndexOf(file);
        if (index < 0)
        {
            pendingScroll = file;
            return;
        }

        pendingScroll = null;
        var line = primary.Map.HeaderLine(index);
        if (line <= 0)
        {
            return;
        }

        PrimeDirty();
        ScrollTo(primary.Editor.TextArea.TextView.GetVisualTopByDocumentLine(line));
    }

    /// <summary>Rebuilds the documents now instead of at the next dispatcher turn.</summary>
    internal void RebuildNow() => Rebuild();

    /// <summary>Re-runs the post-layout steps (height priming, scroll restore, overlay placement); for tests.</summary>
    internal void SettleNow() => AfterLayout();

    /// <summary>Places the overlay controls over the visible rows; for tests.</summary>
    internal void UpdateOverlayNow() => UpdateOverlay();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        Subscribe();
        ActualThemeVariantChanged += OnThemeChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        palette.Refresh(this);
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        Unsubscribe();
        ActualThemeVariantChanged -= OnThemeChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FilesProperty)
        {
            OnFilesChanged();
        }
        else if (change.Property == ModeProperty)
        {
            ApplyLayout();
            Rebuild();
        }
        else if (change.Property == WordWrapProperty)
        {
            primary.Editor.WordWrap = WordWrap && !split;
            secondary.Editor.WordWrap = false;
            ScheduleRebuild();
        }
        else if (change.Property == ReviewProperty)
        {
            reviewController.Configure(Review);
        }
        else if (change.Property == MarkdownPreviewFactoryProperty)
        {
            embeds.Clear();
            ScheduleRebuild();
        }
    }

    private void Subscribe()
    {
        if (subscribed)
        {
            return;
        }

        subscribed = true;
        foreach (var item in items)
        {
            item.PropertyChanged += OnItemChanged;
        }
    }

    private void Unsubscribe()
    {
        if (!subscribed)
        {
            return;
        }

        subscribed = false;
        foreach (var item in items)
        {
            item.PropertyChanged -= OnItemChanged;
        }
    }

    private void OnFilesChanged()
    {
        // The new items are new instances for the same paths (reload, refresh after a revert): keep what the user did to the
        // view. Expanded folds are carried by path; the scroll anchor is captured while the old map still matches.
        pendingAnchor ??= CaptureAnchor();
        Dictionary<string, HashSet<int>>? carried = null;
        foreach (var (item, folds) in expanded)
        {
            if (folds.Count > 0)
            {
                (carried ??= new(StringComparer.Ordinal))[item.Path] = folds;
            }
        }

        Unsubscribe();
        expanded.Clear();
        embeds.Clear();
        indexOf.Clear();
        dirty.Clear();
        items = Files ?? [];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            indexOf[item] = i;
            if (carried is not null && carried.TryGetValue(item.Path, out var folds))
            {
                folds.IntersectWith(DiffDocumentBuilder.FoldKeys(item.Diff));
                if (folds.Count > 0)
                {
                    expanded[item] = folds;

                    // Stretches the user had opened are not loaded in the reread diff: load them again (the Diff change
                    // rebuilds just this file).
                    if (!item.HasFullContext && item.CanLoadContext)
                    {
                        _ = item.LoadFullContextAsync();
                    }
                }
            }
        }

        if (attached)
        {
            Subscribe();
        }

        Rebuild();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        // IsViewed alone changes nothing the document shows: the header binds to it, and ticking it collapses via IsCollapsed.
        if (e.PropertyName is null or nameof(IDiffFileItem.IsCollapsed) or nameof(IDiffFileItem.IsMarkdownPreview)
                or nameof(IDiffFileItem.Blocks) or nameof(IDiffFileItem.Diff)
            && sender is IDiffFileItem item && indexOf.TryGetValue(item, out var index))
        {
            dirty.Add(index);
            PostFlush();
        }
    }

    private void ScheduleRebuild()
    {
        fullPending = true;
        PostFlush();
    }

    private void PostFlush()
    {
        if (flushPending)
        {
            return;
        }

        flushPending = true;
        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Normal);
    }

    private void Flush()
    {
        flushPending = false;
        if (fullPending || dirty.Count > Math.Max(8, items.Count / 8))
        {
            Rebuild();
        }
        else if (dirty.Count > 0)
        {
            RebuildFiles();
        }
    }

    private void ApplyLayout()
    {
        split = Mode == DiffViewMode.Split;
        SecondaryEditor.IsVisible = split;
        SplitDivider.IsVisible = split;
        EditorsGrid.ColumnDefinitions[0].Width = GridLength.Star;
        EditorsGrid.ColumnDefinitions[1].Width = GridLength.Auto;
        EditorsGrid.ColumnDefinitions[2].Width = split ? GridLength.Star : new GridLength(0);
        PrimeScrollbars();
        primary.Editor.WordWrap = WordWrap && !split;
        secondary.Editor.WordWrap = false;
    }

    private void PrimeScrollbars()
    {
        var bar = split ? Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        PrimaryEditor.VerticalScrollBarVisibility = bar;
        SecondaryEditor.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
    }

    private int IndexOf(IDiffFileItem file) => indexOf.TryGetValue(file, out var index) ? index : -1;

    private int IndexOf(Anchor anchor)
    {
        if (IndexOf(anchor.Item) is var index and >= 0)
        {
            return index;
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Path, anchor.Path, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    // ---------------------------------------------------------------- build

    private void Rebuild()
    {
        fullPending = false;
        dirty.Clear();
        primeFiles.Clear();
        primeAll = true;
        pendingAnchor ??= CaptureAnchor();
        RecycleAll();
        var specs = new List<DiffFileSpec>(items.Count);
        var factory = MarkdownPreviewFactory;
        var widths = Math.Max(200, Bounds.Width > 0 ? Bounds.Width : 600);
        embedHeights = new double[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            expanded.TryGetValue(item, out var folds);
            var preview = factory is not null && item.IsMarkdown && item.IsMarkdownPreview;
            specs.Add(new DiffFileSpec(item.Diff, item.IsCollapsed, preview, item.HasFullContext, folds, item.CanLoadContext));
            embedHeights[i] = preview && !item.IsCollapsed ? MeasureEmbed(item, factory!, widths) : 0;
        }

        var built = DiffDocumentBuilder.Build(specs, split);
        FilesBuilt += items.Count;
        var dark = IsDark();
        diffs = new FileDiff[items.Count];
        for (var i = 0; i < diffs.Length; i++)
        {
            diffs[i] = items[i].Diff;
        }

        building = true;
        try
        {
            Apply(primary, built.Primary, diffs, split ? DiffGutterSide.Old : DiffGutterSide.Inline, dark);
            if (built.Secondary is { } right)
            {
                Apply(secondary, right, diffs, DiffGutterSide.New, dark);
            }
            else
            {
                secondary.Map = DiffRowMap.Empty;
                secondary.Editor.Document = new TextDocument();
            }
        }
        finally
        {
            building = false;
        }

        var reviewSide = split ? secondary : primary;
        EnsureOverlay();
        reviewController.Rebind(reviewSide.Editor.Document, reviewSide.Map, items);
        lastTopFile = -1;
        embedPasses = 0;
        calibrations = 0;
        if (attached)
        {
            Dispatcher.UIThread.Post(AfterLayout, DispatcherPriority.Loaded);
        }
    }

    private bool IsDark() => EditorSettings.Current.IsDarkTheme(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);

    /// <summary>
    /// The height the preview row must reserve. Measured the way the overlay will place it: inside the overlay layer (so the
    /// view's md-* styles, fonts and resources apply; a detached control measures unstyled and too short, which showed as a
    /// clipped preview on the first frame) and at the overlay's width (the text area span, not the whole view).
    /// </summary>
    private double MeasureEmbed(IDiffFileItem item, Func<IDiffFileItem, Control> factory, double fallbackWidth)
    {
        var embed = EmbedFor(item, factory);
        var width = OverlayWidth() ?? fallbackWidth;
        var borrowed = embed.Parent is null && attached;
        if (borrowed)
        {
            embed.Opacity = 0;
            OverlayLayer.Children.Add(embed);
        }

        try
        {
            embed.Width = width;
            embed.Measure(new Size(width, double.PositiveInfinity));
            return embed.DesiredSize.Height;
        }
        finally
        {
            if (borrowed)
            {
                OverlayLayer.Children.Remove(embed);
                embed.Opacity = 1;
            }
        }
    }

    /// <summary>The width overlay controls get (left edge of the first text view to the right edge of the visible one).</summary>
    private double? OverlayWidth()
    {
        var textView = primary.Editor.TextArea.TextView;
        var visibleView = split ? secondary.Editor.TextArea.TextView : textView;
        if (!attached || visibleView.Bounds.Width <= 0
            || textView.TranslatePoint(default, OverlayLayer) is not { } origin
            || visibleView.TranslatePoint(new Point(visibleView.Bounds.Width, 0), OverlayLayer) is not { } rightEdge)
        {
            return null;
        }

        var width = rightEdge.X - origin.X;
        return width > 0 ? width : null;
    }

    /// <summary>
    /// Rebuilds only the files in <see cref="dirty"/>: their rows are replaced inside the existing documents and row maps,
    /// later rows shift, and every other file keeps its rows, embeds and overlay controls.
    /// </summary>
    private void RebuildFiles()
    {
        var files = dirty.Order().ToArray();
        dirty.Clear();
        if (files.Length == 0 || embedHeights.Length != items.Count || diffs.Length != items.Count)
        {
            Rebuild();
            return;
        }

        pendingAnchor ??= CaptureAnchor();
        RecycleFiles(files);
        var factory = MarkdownPreviewFactory;
        var width = Math.Max(200, Bounds.Width > 0 ? Bounds.Width : 600);
        var dark = IsDark();
        building = true;
        try
        {
            foreach (var file in files)
            {
                var item = items[file];
                expanded.TryGetValue(item, out var folds);
                var preview = factory is not null && item.IsMarkdown && item.IsMarkdownPreview;
                embedHeights[file] = preview && !item.IsCollapsed ? MeasureEmbed(item, factory!, width) : 0;
                var spec = new DiffFileSpec(item.Diff, item.IsCollapsed, preview, item.HasFullContext, folds, item.CanLoadContext);
                var (left, right) = DiffDocumentBuilder.BuildOne(spec, file, split);
                FilesBuilt++;
                diffs[file] = item.Diff;
                ReplaceFile(primary, left, file, split ? DiffGutterSide.Old : DiffGutterSide.Inline, dark);
                if (right is not null)
                {
                    ReplaceFile(secondary, right, file, DiffGutterSide.New, dark);
                }

                primeFiles.Add(file);
            }
        }
        finally
        {
            building = false;
        }

        var reviewSide = split ? secondary : primary;
        reviewController.Rebind(reviewSide.Editor.Document, reviewSide.Map, items);
        embedPasses = 0;
        if (attached)
        {
            Dispatcher.UIThread.Post(AfterLayout, DispatcherPriority.Loaded);
        }
    }

    private void ReplaceFile(Side side, DiffFileChunk chunk, int file, DiffGutterSide gutter, bool dark)
    {
        var document = side.Editor.Document;
        var old = side.Map;
        var first = old.HeaderLine(file);
        var last = old.FileLastLine(file);
        var map = old.WithFile(file, chunk.Rows);
        side.Map = map;
        side.Generator.Configure(map, embedHeights);
        side.Colorizer.Configure(map, diffs, gutter, dark);
        side.Margin.UpdateMap(map, chunk.Rows);
        var start = document.GetLineByNumber(first).Offset;
        var end = document.GetLineByNumber(last).EndOffset;
        document.Replace(start, end - start, chunk.Text);
    }

    private void RecycleFiles(int[] files)
    {
        List<(DiffRowKind, int, int)>? gone = null;
        foreach (var key in active.Keys)
        {
            if (Array.IndexOf(files, key.Item2) >= 0)
            {
                (gone ??= []).Add(key);
            }
        }

        if (gone is null)
        {
            return;
        }

        foreach (var key in gone)
        {
            Recycle(key.Item1, active[key]);
            active.Remove(key);
        }
    }

    private void Apply(Side side, DiffDocumentModel model, FileDiff[] diffs, DiffGutterSide gutter, bool dark)
    {
        side.Map = model.Map;
        side.Generator.Configure(model.Map, embedHeights);
        side.Colorizer.Configure(model.Map, diffs, gutter, dark);
        side.Margin.SetMap(model.Map, gutter);
        var document = new TextDocument(model.Text);
        document.UndoStack.SizeLimit = 0;
        side.Editor.Document = document;
    }

    private Control EmbedFor(IDiffFileItem item, Func<IDiffFileItem, Control> factory)
    {
        if (!embeds.TryGetValue(item, out var control))
        {
            control = factory(item);
            AutomationProperties.SetAutomationId(control, "diff-markdown-preview");
            // The preview spans both columns in split mode; an opaque surface keeps the split divider from showing through.
            if (control is Avalonia.Controls.Primitives.TemplatedControl { Background: null } templated)
            {
                templated.Bind(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty, this.GetResourceObservable("SurfaceBrush"));
            }

            embeds[item] = control;
        }

        return control;
    }

    private void EnsureOverlay()
    {
        var editor = split ? SecondaryEditor : PrimaryEditor;
        if (overlay is not null && ReferenceEquals(overlayEditor, editor))
        {
            return;
        }

        overlay?.Dispose();
        overlayEditor = editor;
        overlay = new ReviewOverlay(editor, new ReviewOverlayParts
        {
            Layer = ReviewLayer,
            Icon = ReviewIconButton,
            Composer = ReviewComposer,
            RangeText = ReviewRangeText,
            BodyBox = ReviewBodyBox,
            ComposerStatus = ReviewComposerStatus,
            SaveButton = ReviewSaveButton,
            SendButton = ReviewSendButton,
            DeleteButton = ReviewDeleteButton,
            CancelButton = ReviewCancelButton,
            SendAllButton = ReviewSendAll,
            StatusText = ReviewStatus,
        });
        overlay.Bind(reviewController);
    }

    // ----------------------------------------------------- scroll and layout

    private sealed record Anchor(IDiffFileItem Item, string Path, int RelativeLine, double Pixel);

    private Anchor? CaptureAnchor()
    {
        try
        {
            var textView = primary.Editor.TextArea.TextView;
            var y = textView.ScrollOffset.Y;
            if (y <= 0 || primary.Map.Count == 0 || textView.Document is null)
            {
                return null;
            }

            if (textView.GetDocumentLineByVisualTop(y) is not { } line)
            {
                return null;
            }

            var file = primary.Map.FileAt(line.LineNumber);
            if ((uint)file >= (uint)items.Count)
            {
                return null;
            }

            var top = textView.GetVisualTopByDocumentLine(line.LineNumber);
            return new Anchor(items[file], items[file].Path, line.LineNumber - primary.Map.HeaderLine(file), y - top);
        }
        catch (VisualLinesInvalidException)
        {
            return null;
        }
    }

    private void AfterLayout()
    {
        if (calibrations < 3 && Calibrate())
        {
            // The placeholder elements were rebuilt at their corrected height; finish once that layout has run.
            Dispatcher.UIThread.Post(AfterLayout, DispatcherPriority.Loaded);
            return;
        }

        PrimeDirty();
        if (pendingScroll is { } file)
        {
            ScrollToFile(file);
        }
        else if (pendingAnchor is { } anchor)
        {
            var index = IndexOf(anchor);
            if (index >= 0)
            {
                var header = primary.Map.HeaderLine(index);
                var line = Math.Clamp(header + anchor.RelativeLine, header, Math.Max(header, primary.Map.FileLastLine(index)));
                ScrollTo(primary.Editor.TextArea.TextView.GetVisualTopByDocumentLine(line) + anchor.Pixel);
            }
        }

        pendingAnchor = null;
        UpdateOverlay();
        ReportTopFile();
    }

    /// <summary>Scrolls the editor's viewport to the vertical offset <paramref name="y"/> (clamped by the scroll viewer).</summary>
    internal static void SetOffset(TextEditor editor, double y)
    {
        if (editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } viewer)
        {
            viewer.Offset = new Vector(viewer.Offset.X, Math.Max(0, y));
        }
        else
        {
            editor.ScrollToVerticalOffset(y);
        }
    }

    /// <summary>Scrolls the shown document to vertical offset <paramref name="y"/>; for tests.</summary>
    internal void ScrollToOffset(double y) => ScrollTo(y);

    private void ScrollTo(double y)
    {
        syncing = true;
        try
        {
            SetOffset(primary.Editor, y);
            if (split)
            {
                SetOffset(secondary.Editor, y);
            }
        }
        finally
        {
            syncing = false;
        }

        UpdateOverlay();
        ReportTopFile();
    }

    /// <summary>
    /// A line holding an inline object is taller than the object by what the text below the baseline needs. Reads that surplus
    /// from the first placeholder line on screen and rebuilds the placeholders so that each line is exactly its row height.
    /// </summary>
    private bool Calibrate()
    {
        calibrations++;
        var changed = false;
        foreach (var side in split ? new[] { primary, secondary } : new[] { primary })
        {
            var textView = side.Editor.TextArea.TextView;
            if (side.Editor.Document is null || !textView.VisualLinesValid)
            {
                continue;
            }

            foreach (var visual in textView.VisualLines)
            {
                var number = visual.FirstDocumentLine.LineNumber;
                if (!side.Map.Contains(number) || !side.Map.ForLine(number).IsPlaceholder)
                {
                    continue;
                }

                var row = side.Map.ForLine(number);
                var element = Math.Max(1, Math.Ceiling(DiffPlaceholderGenerator.HeightOf(row, embedHeights) - side.Generator.Compensation));
                var surplus = Math.Max(0, visual.Height - element);
                if (Math.Abs(surplus - side.Generator.Compensation) > 0.25)
                {
                    side.Generator.Compensation = surplus;
                    textView.Redraw();
                    changed = true;
                }

                break;
            }
        }

        return changed;
    }

    /// <summary>
    /// Constructs the visual line of the placeholder lines that are new since the last priming (every one after a full
    /// rebuild, only the rebuilt files' after a per-file change), so the height tree knows their real heights and the scroll
    /// extent (and every offset computed from it) is right before those lines are first scrolled into view.
    /// </summary>
    private void PrimeDirty()
    {
        if (primeAll)
        {
            var done = PrimeSide(primary, 1, primary.Map.Count);
            if (split)
            {
                done &= PrimeSide(secondary, 1, secondary.Map.Count);
            }

            primeAll = !done;
            primeFiles.Clear();
            return;
        }

        foreach (var file in primeFiles)
        {
            var done = PrimeSide(primary, primary.Map.HeaderLine(file), primary.Map.FileLastLine(file));
            if (split)
            {
                done &= PrimeSide(secondary, secondary.Map.HeaderLine(file), secondary.Map.FileLastLine(file));
            }

            if (!done)
            {
                primeAll = true;
                break;
            }
        }

        primeFiles.Clear();
    }

    private static bool PrimeSide(Side side, int from, int to)
    {
        var textView = side.Editor.TextArea.TextView;
        var document = side.Editor.Document;
        if (document is null || !textView.IsMeasureValid && textView.Bounds.Width <= 0)
        {
            return false;
        }

        try
        {
            to = Math.Min(to, side.Map.Count);
            for (var n = Math.Max(1, from); n <= to; n++)
            {
                if (side.Map[n - 1].IsPlaceholder)
                {
                    textView.GetOrConstructVisualLine(document.GetLineByNumber(n));
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or VisualLinesInvalidException or ArgumentException)
        {
            System.Diagnostics.Trace.WriteLine("[diff] height priming skipped: " + exception.Message);
        }

        return true;
    }

    private void OnScrollChanged(object? sender, EventArgs e)
    {
        if (building)
        {
            return;
        }

        if (sender is TextView view)
        {
            if (!syncing && split)
            {
                var from = ReferenceEquals(view, primary.Editor.TextArea.TextView) ? primary : secondary;
                var to = ReferenceEquals(from, primary) ? secondary : primary;
                if (Math.Abs(to.Editor.TextArea.TextView.ScrollOffset.Y - view.ScrollOffset.Y) > 0.5)
                {
                    syncing = true;
                    try
                    {
                        SetOffset(to.Editor, view.ScrollOffset.Y);
                    }
                    finally
                    {
                        syncing = false;
                    }
                }
            }
        }

        UpdateOverlay();
        ReportTopFile();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e)
    {
        if (building)
        {
            return;
        }

        UpdateOverlay();
        ReportTopFile();
    }

    private void ReportTopFile()
    {
        var textView = primary.Editor.TextArea.TextView;
        if (textView.Document is null || primary.Map.Count == 0)
        {
            return;
        }

        var line = textView.GetDocumentLineByVisualTop(textView.ScrollOffset.Y + 1);
        if (line is null)
        {
            return;
        }

        var file = primary.Map.FileAt(line.LineNumber);
        if (file < 0 || file == lastTopFile || file >= items.Count)
        {
            return;
        }

        lastTopFile = file;
        TopFileChanged?.Invoke(items[file]);
    }

    // -------------------------------------------------------------- overlay

    private void RecycleAll()
    {
        foreach (var ((kind, _, _), control) in active)
        {
            Recycle(kind, control);
        }

        active.Clear();
    }

    private void Recycle(DiffRowKind kind, Control control)
    {
        if (kind != DiffRowKind.Embed)
        {
            control.DataContext = null;
            OverlayLayer.Children.Remove(control);
        }
        else
        {
            OverlayLayer.Children.Remove(control);
        }

        switch (control)
        {
            case DiffFileHeader header:
                headerPool.Push(header);
                break;
            case DiffBlockBar bar:
                barPool.Push(bar);
                break;
            case DiffFoldBar fold:
                fold.ExpandRequested -= OnFoldExpand;
                foldPool.Push(fold);
                break;
        }
    }

    private void UpdateOverlay()
    {
        var textView = primary.Editor.TextArea.TextView;
        if (!attached || textView.Document is null || !textView.VisualLinesValid || items.Count == 0)
        {
            return;
        }

        var visibleView = split ? secondary.Editor.TextArea.TextView : textView;
        if (textView.TranslatePoint(default, OverlayLayer) is not { } origin
            || visibleView.TranslatePoint(new Point(visibleView.Bounds.Width, 0), OverlayLayer) is not { } rightEdge)
        {
            return;
        }

        var width = Math.Max(0, rightEdge.X - origin.X);
        var scrollY = textView.ScrollOffset.Y;
        var needed = new HashSet<(DiffRowKind, int, int)>();
        var heightsChanged = false;
        foreach (var visual in textView.VisualLines)
        {
            var number = visual.FirstDocumentLine.LineNumber;
            if (!primary.Map.Contains(number))
            {
                continue;
            }

            var row = primary.Map.ForLine(number);
            if (!row.IsPlaceholder || (uint)row.File >= (uint)items.Count)
            {
                continue;
            }

            var key = (row.Kind, row.File, row.Kind == DiffRowKind.BlockBar ? row.Block : row.Kind == DiffRowKind.Fold ? row.Key : 0);
            needed.Add(key);
            var item = items[row.File];
            if (!active.TryGetValue(key, out var control))
            {
                control = Acquire(row, item);
                if (control is null)
                {
                    continue;
                }

                active[key] = control;
                OverlayLayer.Children.Add(control);
            }

            var top = origin.Y + visual.VisualTop - scrollY;
            control.Width = width;
            if (row.Kind == DiffRowKind.Embed)
            {
                control.Measure(new Size(width, double.PositiveInfinity));
                var measured = control.DesiredSize.Height;
                if (Math.Abs(measured - embedHeights[row.File]) > 0.5)
                {
                    embedHeights[row.File] = measured;
                    primeFiles.Add(row.File);
                    heightsChanged = true;
                }

                control.Height = measured;
            }
            else
            {
                control.Height = visual.Height;
            }

            Canvas.SetLeft(control, origin.X);
            Canvas.SetTop(control, top);
        }

        List<(DiffRowKind, int, int)>? stale = null;
        foreach (var key in active.Keys)
        {
            if (!needed.Contains(key))
            {
                (stale ??= []).Add(key);
            }
        }

        if (stale is not null)
        {
            foreach (var key in stale)
            {
                Recycle(key.Item1, active[key]);
                active.Remove(key);
            }
        }

        if (heightsChanged && embedPasses++ < 3)
        {
            primary.Editor.TextArea.TextView.Redraw();
            if (split)
            {
                secondary.Editor.TextArea.TextView.Redraw();
            }

            Dispatcher.UIThread.Post(() =>
            {
                PrimeDirty();
                UpdateOverlay();
            }, DispatcherPriority.Loaded);
        }
    }

    private Control? Acquire(in DiffRow row, IDiffFileItem item)
    {
        switch (row.Kind)
        {
            case DiffRowKind.FileHeader:
            {
                var header = headerPool.Count > 0 ? headerPool.Pop() : new DiffFileHeader();
                header.DataContext = item;
                AutomationProperties.SetAutomationId(header, "diff-file-header");
                AutomationProperties.SetName(header, Loc.Instance.Format(DiffStrings.FileHeader, item.Path));
                return header;
            }

            case DiffRowKind.BlockBar:
            {
                if ((uint)row.Block >= (uint)item.Blocks.Count)
                {
                    return null;
                }

                var bar = barPool.Count > 0 ? barPool.Pop() : new DiffBlockBar();
                bar.DataContext = item.Blocks[row.Block];
                AutomationProperties.SetAutomationId(bar, "diff-block-bar");
                AutomationProperties.SetName(bar, item.Path + "#" + row.Block.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return bar;
            }

            case DiffRowKind.Fold:
            {
                var fold = foldPool.Count > 0 ? foldPool.Pop() : new DiffFoldBar();
                fold.Bind(row);
                fold.ExpandRequested += OnFoldExpand;
                return fold;
            }

            case DiffRowKind.Embed:
                return MarkdownPreviewFactory is { } factory ? EmbedFor(item, factory) : null;
            default:
                return null;
        }
    }

    private void OnFoldExpand(object? sender, EventArgs e)
    {
        if (sender is not DiffFoldBar fold || (uint)fold.FileIndex >= (uint)items.Count || !fold.CanExpand)
        {
            return;
        }

        var item = items[fold.FileIndex];
        if (!expanded.TryGetValue(item, out var set))
        {
            expanded[item] = set = [];
        }

        set.Add(fold.Key);
        if (!item.HasFullContext)
        {
            // An unloaded stretch: the whole file is read first; its Diff change rebuilds this file with the fold open.
            _ = item.LoadFullContextAsync();
            return;
        }

        dirty.Add(fold.FileIndex);
        RebuildFiles();
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        var editor = split ? SecondaryEditor : PrimaryEditor;
        var textView = editor.TextArea.TextView;
        var target = Math.Max(0, textView.ScrollOffset.Y - e.Delta.Y * textView.DefaultLineHeight * 3);
        SetOffset(editor, target);
        e.Handled = true;
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        palette.Refresh(this);
        Rebuild();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RecycleAll();
        UpdateOverlay();
    }

    // ----------------------------------------------------------------- copy

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextEditor editor
            || !(e.KeyModifiers == KeyModifiers.Control && e.Key is Key.C or Key.Insert))
        {
            return;
        }

        var side = ReferenceEquals(editor, secondary.Editor) ? secondary : primary;
        e.Handled = true;
        if (editor.TextArea.Selection.IsEmpty || editor.Document is not { } document)
        {
            return;
        }

        var segment = editor.TextArea.Selection.SurroundingSegment;
        var text = DiffSelectionText.Extract(document, side.Map, segment.Offset, segment.EndOffset);
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = SetClipboardAsync(clipboard, text);
        }
    }

    private static async Task SetClipboardAsync(Avalonia.Input.Platform.IClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            System.Diagnostics.Trace.WriteLine("[diff] copy failed: " + exception.Message);
        }
    }

    // ----------------------------------------------------------------- side

    private sealed class Side
    {
        public Side(TextEditor editor, DiffPalette palette, DiffGutterSide gutter)
        {
            Editor = editor;
            Margin = new DiffLineNumberMargin(palette, gutter);
            Generator = new DiffPlaceholderGenerator();
            Colorizer = new DiffSyntaxColorizer();
            var area = editor.TextArea;
            area.LeftMargins.Insert(0, Margin);
            area.TextView.ElementGenerators.Add(Generator);
            area.TextView.LineTransformers.Add(Colorizer);
            area.TextView.BackgroundRenderers.Add(new DiffBackgroundRenderer(() => Map, palette));
            editor.Options.EnableHyperlinks = false;
            editor.Options.EnableEmailHyperlinks = false;
            editor.Options.HighlightCurrentLine = false;
            editor.Options.ShowBoxForControlCharacters = false;
        }

        public TextEditor Editor { get; }

        public DiffLineNumberMargin Margin { get; }

        public DiffPlaceholderGenerator Generator { get; }

        public DiffSyntaxColorizer Colorizer { get; }

        public DiffRowMap Map { get; set; } = DiffRowMap.Empty;
    }
}
