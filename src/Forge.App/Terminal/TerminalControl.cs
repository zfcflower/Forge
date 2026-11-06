using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Terminal.Vt;
using VtMouseButton = Forge.App.Terminal.Vt.MouseButton;

namespace Forge.App.Terminal;

/// <summary>
/// The terminal surface: a basic <see cref="Control"/> that paints the emulator's visible rows with
/// <see cref="DrawingContext"/> instead of composing controls. Cell width and height come from the code-font preference
/// (so a font change re-measures and repaints immediately), colours from <see cref="TerminalPalette"/> plus the theme's
/// text and surface tokens, and each row's text runs are cached by line object and line version, so a frame only rebuilds
/// the rows that actually changed.
/// <para>The control never parses: it takes <see cref="TerminalViewModel.Gate"/> briefly to read the visible rows and to
/// encode input, and the background pump keeps feeding the emulator on its own thread.</para>
/// </summary>
internal sealed class TerminalControl : Control
{
    public static readonly StyledProperty<TerminalViewModel?> SourceProperty =
        AvaloniaProperty.Register<TerminalControl, TerminalViewModel?>(nameof(Source));

    /// <summary>How long a size change waits before the emulator and the pseudo-terminal are resized.</summary>
    private static readonly TimeSpan ResizeDebounce = TimeSpan.FromMilliseconds(50);

    /// <summary>Rows scrolled per wheel notch when the wheel goes to the scrollback.</summary>
    private const int WheelScrollLines = 3;

    /// <summary>The highest rune the ASCII-only range covers; above it a rune may come from a fallback font.</summary>
    private const int AsciiLimit = 0x7E;

    private static readonly bool IsMacOs = OperatingSystem.IsMacOS();

    private static readonly Color LightForeground = Color.Parse("#2B2F2C");
    private static readonly Color LightBackground = Color.Parse("#F5F6F5");
    private static readonly Color SelectionTint = Color.Parse("#3A8F5C");

    private static readonly TextDecorationCollection Underline = TextDecorations.Underline;
    private static readonly TextDecorationCollection Strikethrough = TextDecorations.Strikethrough;
    private static readonly TextDecorationCollection UnderlineAndStrikethrough =
    [
        new TextDecoration { Location = TextDecorationLocation.Underline },
        new TextDecoration { Location = TextDecorationLocation.Strikethrough },
    ];

    private readonly Dictionary<uint, IBrush> brushes = [];
    private readonly DispatcherTimer resizeTimer;

    private LinePaint?[] linePaints = [];
    private Typeface regular = Typeface.Default;
    private Typeface bold = new(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.Bold);
    private Typeface italic = new(Typeface.Default.FontFamily, FontStyle.Italic, FontWeight.Normal);
    private Typeface boldItalic = new(Typeface.Default.FontFamily, FontStyle.Italic, FontWeight.Bold);

    private Color themeForeground = LightForeground;
    private Color themeBackground = LightBackground;
    private TerminalPalette palette = TerminalPalette.For(dark: false);

    private double cellWidth = 8;
    private double cellHeight = 16;

    private TerminalViewModel? boundSource;
    private AppPreferences? boundPreferences;
    private MenuItem? copyItem;
    private MenuItem? pasteItem;
    private MenuItem? clearItem;
    private bool attached;

    private int scrollOffset;
    private long anchoredPushed;
    private long anchoredDropped;
    private (int Line, int Column) selectionStart;
    private (int Line, int Column) selectionEnd;
    private bool hasSelection;
    private bool selecting;
    private bool forwardedPress;
    private bool hasMotionCell;
    private (int Column, int Row) motionCell;

    private int pendingColumns = TerminalViewModel.InitialColumns;
    private int pendingRows = TerminalViewModel.InitialRows;
    private bool gridPending;

    static TerminalControl()
    {
        SourceProperty.Changed.AddClassHandler<TerminalControl>((control, _) => control.OnSourceChanged());
        BoundsProperty.Changed.AddClassHandler<TerminalControl>((control, _) => control.RequestGrid());
    }

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        AutomationProperties.SetAutomationId(this, "terminal-surface");
        AutomationProperties.SetName(this, Loc.Instance[TerminalStrings.AutomationSurface]);

        resizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ResizeDebounce };
        resizeTimer.Tick += OnResizeTick;

        // The cursor is hollow while the terminal is not focused, so focus changes repaint it.
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();

        var menu = new ContextMenu();
        copyItem = new MenuItem { Header = Loc.Instance[TerminalStrings.MenuCopy] };
        copyItem.Click += (_, _) => CopySelection();
        pasteItem = new MenuItem { Header = Loc.Instance[TerminalStrings.MenuPaste] };
        pasteItem.Click += (_, _) => _ = PasteAsync();
        clearItem = new MenuItem { Header = Loc.Instance[TerminalStrings.MenuClear] };
        clearItem.Click += (_, _) => ClearScreen();
        menu.Items.Add(copyItem);
        menu.Items.Add(pasteItem);
        menu.Items.Add(clearItem);
        ContextMenu = menu;
    }

    /// <summary>The terminal this surface shows; the tab's data template binds it.</summary>
    public TerminalViewModel? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    // ---------------------------------------------------------------- lifetime and subscriptions

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        ActualThemeVariantChanged += OnThemeVariantChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        SubscribePreferences();
        SubscribeSource();
        UpdateMetrics();
        UpdateTheme();
        RefreshMenuText();
        RequestGrid();
        Focus();
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        resizeTimer.Stop();
        ActualThemeVariantChanged -= OnThemeVariantChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        UnsubscribePreferences();
        UnsubscribeSource();
        Source?.AttachClipboard(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSourceChanged()
    {
        if (attached)
        {
            UnsubscribeSource();
            SubscribeSource();
            UpdateMetrics();
            UpdateTheme();
        }

        ClearLinePaints();
        selectionStart = default;
        selectionEnd = default;
        hasSelection = false;
        scrollOffset = 0;
        hasMotionCell = false;
        ResetScrollAnchor();

        // The new view model's emulator may still have another size, so the grid is pushed once even when the view itself
        // did not change size.
        pendingColumns = 0;
        pendingRows = 0;
        RequestGrid();
        InvalidateVisual();
    }

    private void SubscribeSource()
    {
        if (Source is not { } viewModel || ReferenceEquals(boundSource, viewModel))
        {
            return;
        }

        boundSource = viewModel;
        viewModel.Invalidated += OnInvalidated;
        viewModel.TitleChanged += OnInvalidated;
        viewModel.AttachClipboard(TopLevel.GetTopLevel(this)?.Clipboard);
    }

    private void UnsubscribeSource()
    {
        if (boundSource is { } viewModel)
        {
            viewModel.Invalidated -= OnInvalidated;
            viewModel.TitleChanged -= OnInvalidated;
            boundSource = null;
        }
    }

    private void SubscribePreferences()
    {
        if (Source?.Preferences is not { } preferences || ReferenceEquals(boundPreferences, preferences))
        {
            return;
        }

        boundPreferences = preferences;
        preferences.PropertyChanged += OnPreferenceChanged;
    }

    private void UnsubscribePreferences()
    {
        if (boundPreferences is { } preferences)
        {
            preferences.PropertyChanged -= OnPreferenceChanged;
            boundPreferences = null;
        }
    }

    private void OnInvalidated(object? sender, EventArgs e) => InvalidateVisual();

    private void OnPreferenceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppPreferences.CodeFontFamily) or nameof(AppPreferences.CodeFontSize))
        {
            UpdateMetrics();
            return;
        }

        if (e.PropertyName == nameof(AppPreferences.TerminalScrollbackLines) && Source is { } viewModel)
        {
            lock (viewModel.Gate)
            {
                scrollOffset = Math.Clamp(scrollOffset, 0, viewModel.Emulator.ScrollbackCount);
            }

            InvalidateVisual();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        AutomationProperties.SetName(this, Loc.Instance[TerminalStrings.AutomationSurface]);
        RefreshMenuText();
    }

    private void RefreshMenuText()
    {
        if (copyItem is not null)
        {
            copyItem.Header = Loc.Instance[TerminalStrings.MenuCopy];
        }

        if (pasteItem is not null)
        {
            pasteItem.Header = Loc.Instance[TerminalStrings.MenuPaste];
        }

        if (clearItem is not null)
        {
            clearItem.Header = Loc.Instance[TerminalStrings.MenuClear];
        }
    }

    // ---------------------------------------------------------------- metrics, grid and theme

    private void UpdateMetrics()
    {
        var preferences = Source?.Preferences;
        var family = FontFamily.Parse(preferences?.CodeFontFamily ?? AppPreferences.DefaultCodeFontFamily);
        var size = preferences?.CodeFontSize ?? AppPreferences.DefaultCodeFontSize;
        regular = new Typeface(family, FontStyle.Normal, FontWeight.Normal);
        bold = new Typeface(family, FontStyle.Normal, FontWeight.Bold);
        italic = new Typeface(family, FontStyle.Italic, FontWeight.Normal);
        boldItalic = new Typeface(family, FontStyle.Italic, FontWeight.Bold);

        var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, regular, size, Brushes.Black);
        cellWidth = Math.Max(1, probe.WidthIncludingTrailingWhitespace);
        cellHeight = Math.Max(1, Math.Ceiling(probe.Height));

        ClearLinePaints();
        InvalidateMeasure();
        RequestGrid();
        InvalidateVisual();
    }

    private void UpdateTheme()
    {
        var dark = Equals(ActualThemeVariant, ThemeVariant.Dark);
        palette = TerminalPalette.For(dark);
        var foreground = palette.FallbackForeground;
        var background = palette.FallbackBackground;
        if (this.TryFindResource("TextPrimaryBrush", ActualThemeVariant, out var foregroundValue) &&
            foregroundValue is ISolidColorBrush foregroundBrush)
        {
            foreground = foregroundBrush.Color;
        }

        if (this.TryFindResource("SurfaceBackgroundBrush", ActualThemeVariant, out var backgroundValue) &&
            backgroundValue is ISolidColorBrush backgroundBrush)
        {
            background = backgroundBrush.Color;
        }

        themeForeground = foreground;
        themeBackground = background;
        brushes.Clear();
        ClearLinePaints();
        Source?.SetDefaultColors(
            TerminalColor.Rgb(foreground.R, foreground.G, foreground.B),
            TerminalColor.Rgb(background.R, background.G, background.B));
        InvalidateVisual();
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e) => UpdateTheme();

    private void RequestGrid()
    {
        if (cellWidth <= 0 || cellHeight <= 0 || !(Bounds.Width > 0) || !(Bounds.Height > 0))
        {
            return;
        }

        var columns = Math.Max(1, (int)Math.Floor(Bounds.Width / cellWidth));
        var rows = Math.Max(1, (int)Math.Floor(Bounds.Height / cellHeight));
        if (columns == pendingColumns && rows == pendingRows && !gridPending)
        {
            return;
        }

        pendingColumns = columns;
        pendingRows = rows;
        gridPending = true;
        resizeTimer.Stop();
        resizeTimer.Start();
    }

    private void OnResizeTick(object? sender, EventArgs e)
    {
        resizeTimer.Stop();
        if (!gridPending)
        {
            return;
        }

        gridPending = false;
        Source?.Resize(pendingColumns, pendingRows);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(
            double.IsFinite(availableSize.Width) ? availableSize.Width : cellWidth * 80,
            double.IsFinite(availableSize.Height) ? availableSize.Height : cellHeight * 24);

    protected override Size ArrangeOverride(Size finalSize)
    {
        RequestGrid();
        return finalSize;
    }

    // ---------------------------------------------------------------- rendering

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(BrushFor(themeBackground), new Rect(Bounds.Size));
        if (Source is not { } viewModel || cellWidth <= 0 || cellHeight <= 0)
        {
            return;
        }

        var colors = palette;
        lock (viewModel.Gate)
        {
            var emulator = viewModel.Emulator;

            // Output may have arrived since the last frame: anchor the viewport and the selection before reading rows.
            SyncScrollAnchor(emulator);

            var columns = emulator.Columns;
            var rows = emulator.Rows;
            var total = emulator.TotalLines;
            var offset = Math.Clamp(scrollOffset, 0, emulator.ScrollbackCount);
            var topLine = emulator.ScrollbackCount - offset;
            var reverse = (emulator.Modes & TerminalModes.ReverseVideo) != 0;

            EnsureLinePaints(rows);
            for (var row = 0; row < rows; row++)
            {
                var index = topLine + row;
                if (index < 0 || index >= total)
                {
                    continue;
                }

                DrawLine(context, emulator.GetLine(index), row, index, columns, colors, reverse);
            }

            var cursor = emulator.Cursor;
            if (cursor.Visible && offset == 0 && cursor.Row < rows && cursor.Column < columns)
            {
                DrawCursor(context, cursor);
            }
        }
    }

    private void DrawLine(
        DrawingContext context,
        TerminalLine line,
        int row,
        int absolute,
        int columns,
        TerminalPalette palette,
        bool reverse)
    {
        var y = row * cellHeight;

        // Backgrounds: only cells that differ from the theme surface need a rectangle, so a normal screen draws none.
        var runStart = -1;
        var runColor = themeBackground;
        for (var column = 0; column <= columns; column++)
        {
            var color = themeBackground;
            if (column < columns && column < line.Count)
            {
                color = BackgroundOf(line[column], palette, reverse);
            }

            if (runStart >= 0 && color != runColor)
            {
                Fill(context, runColor, runStart, column, y);
                runStart = -1;
            }

            if (runStart < 0 && color != themeBackground)
            {
                runStart = column;
                runColor = color;
            }
        }

        DrawSelection(context, absolute, row, columns);

        var paint = linePaints[row];
        if (paint is null || !ReferenceEquals(paint.Line, line) || paint.Version != line.Version || paint.Reverse != reverse)
        {
            paint = BuildLinePaint(line, palette, reverse);
            linePaints[row] = paint;
        }

        for (var i = 0; i < paint.Runs.Count; i++)
        {
            LineRun run = paint.Runs[i];
            context.DrawText(run.Text, new Point(run.StartColumn * cellWidth, y));
        }
    }

    private void Fill(DrawingContext context, Color color, int startColumn, int endColumn, double y) =>
        context.FillRectangle(
            BrushFor(color),
            new Rect(startColumn * cellWidth, y, (endColumn - startColumn) * cellWidth, cellHeight));

    private void DrawSelection(DrawingContext context, int absolute, int row, int columns)
    {
        if (!hasSelection)
        {
            return;
        }

        var (from, to) = SelectionOrder();
        if (absolute < from.Line || absolute > to.Line)
        {
            return;
        }

        var start = absolute == from.Line ? from.Column : 0;
        var end = absolute == to.Line ? to.Column : columns - 1;
        start = Math.Clamp(start, 0, Math.Max(0, columns - 1));
        end = Math.Clamp(end, start, Math.Max(0, columns - 1));
        var brush = BrushFor(Color.FromArgb(90, SelectionTint.R, SelectionTint.G, SelectionTint.B));
        context.FillRectangle(brush, new Rect(start * cellWidth, row * cellHeight, (end - start + 1) * cellWidth, cellHeight));
    }

    private void DrawCursor(DrawingContext context, TerminalCursor cursor)
    {
        var x = cursor.Column * cellWidth;
        var y = cursor.Row * cellHeight;
        if (!IsFocused)
        {
            // An unfocused terminal shows a hollow block, the convention every terminal emulator uses.
            context.DrawRectangle(
                null,
                new Pen(BrushFor(themeForeground), 1),
                new Rect(x + 0.5, y + 0.5, Math.Max(1, cellWidth - 1), Math.Max(1, cellHeight - 1)));
            return;
        }

        var brush = BrushFor(Color.FromArgb(120, themeForeground.R, themeForeground.G, themeForeground.B));
        switch (cursor.Shape)
        {
            case CursorShape.Underline:
                context.FillRectangle(brush, new Rect(x, y + cellHeight - 2, cellWidth, 2));
                break;
            case CursorShape.Bar:
                context.FillRectangle(brush, new Rect(x, y, 2, cellHeight));
                break;
            default:
                context.FillRectangle(brush, new Rect(x, y, cellWidth, cellHeight));
                break;
        }
    }

    /// <summary>
    /// Rebuilds the cached text runs of one row: contiguous ASCII cells with the same resolved paint become one run. A
    /// wide character, a non-ASCII rune or a cell carrying combining marks is always its own run, because the fallback
    /// font may draw it at a width that is not an exact multiple of the cell width: a merged run would then drift within
    /// the run, while a standalone run is placed at <c>StartColumn * cellWidth</c> and stays on its grid.
    /// </summary>
    private LinePaint BuildLinePaint(TerminalLine line, TerminalPalette palette, bool reverse)
    {
        var paint = new LinePaint(line, line.Version, reverse, new List<LineRun>(8));
        var text = new StringBuilder();
        var startColumn = -1;
        var runColor = themeForeground;
        var runFlags = CellFlags.None;

        void Flush()
        {
            if (startColumn >= 0 && text.Length > 0)
            {
                paint.Runs.Add(new LineRun(startColumn, CreateText(text.ToString(), runColor, runFlags)));
            }

            startColumn = -1;
            text.Clear();
        }

        for (var column = 0; column < line.Count; column++)
        {
            var cell = line[column];
            if (cell.IsWideContinuation)
            {
                // The glyph of the double-width cell before this one already covers the two columns.
                continue;
            }

            var combining = line.GetCombining(column);
            if (cell.Rune == 0 && combining is null)
            {
                Flush();
                continue;
            }

            if ((cell.Flags & CellFlags.Hidden) != 0)
            {
                Flush();
                continue;
            }

            var color = ForegroundOf(cell, palette, reverse);
            var flags = cell.Flags & (CellFlags.Bold | CellFlags.Italic | CellFlags.Underline |
                                      CellFlags.DoubleUnderline | CellFlags.Strikethrough | CellFlags.Dim);

            if (cell.Rune > AsciiLimit || CellWidth.Of(cell.Rune) == 2 || combining is not null)
            {
                // Its own run, positioned on the grid: a wide or fallback glyph cannot share a run with its neighbours.
                Flush();
                startColumn = column;
                runColor = color;
                runFlags = flags;
                text.Append(char.ConvertFromUtf32(cell.Rune));
                if (combining is not null)
                {
                    text.Append(combining);
                }

                Flush();
                continue;
            }

            if (startColumn >= 0 && (color != runColor || flags != runFlags))
            {
                Flush();
            }

            if (startColumn < 0)
            {
                startColumn = column;
                runColor = color;
                runFlags = flags;
            }

            text.Append((char)cell.Rune);
        }

        Flush();
        return paint;
    }

    private FormattedText CreateText(string value, Color color, CellFlags flags)
    {
        var typeface = flags switch
        {
            _ when (flags & CellFlags.Bold) != 0 && (flags & CellFlags.Italic) != 0 => boldItalic,
            _ when (flags & CellFlags.Bold) != 0 => bold,
            _ when (flags & CellFlags.Italic) != 0 => italic,
            _ => regular,
        };
        var brush = (flags & CellFlags.Dim) != 0
            ? BrushFor(Color.FromArgb(170, color.R, color.G, color.B))
            : BrushFor(color);
        var size = Source?.Preferences.CodeFontSize ?? AppPreferences.DefaultCodeFontSize;
        var text = new FormattedText(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush);
        var underline = (flags & (CellFlags.Underline | CellFlags.DoubleUnderline)) != 0;
        var strike = (flags & CellFlags.Strikethrough) != 0;
        if (underline && strike)
        {
            text.SetTextDecorations(UnderlineAndStrikethrough);
        }
        else if (underline)
        {
            text.SetTextDecorations(Underline);
        }
        else if (strike)
        {
            text.SetTextDecorations(Strikethrough);
        }

        return text;
    }

    private Color ForegroundOf(TerminalCell cell, TerminalPalette palette, bool reverse) =>
        Colors(cell, palette, reverse).Foreground;

    private Color BackgroundOf(TerminalCell cell, TerminalPalette palette, bool reverse) =>
        Colors(cell, palette, reverse).Background;

    /// <summary>
    /// The effective colours of one cell: palette or theme default, inverse video swapping the two, and DECSCNM swapping
    /// the whole screen on top of that.
    /// </summary>
    private (Color Foreground, Color Background) Colors(TerminalCell cell, TerminalPalette palette, bool reverse)
    {
        var foreground = palette.Resolve(cell.Foreground) ?? themeForeground;
        var background = palette.Resolve(cell.Background) ?? themeBackground;
        var swap = ((cell.Flags & CellFlags.Inverse) != 0) ^ reverse;
        return swap ? (background, foreground) : (foreground, background);
    }

    private IBrush BrushFor(Color color)
    {
        var key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        if (!brushes.TryGetValue(key, out var brush))
        {
            if (brushes.Count > 512)
            {
                brushes.Clear();
            }

            brush = new SolidColorBrush(color).ToImmutable();
            brushes[key] = brush;
        }

        return brush;
    }

    private void EnsureLinePaints(int rows)
    {
        if (linePaints.Length != rows)
        {
            linePaints = new LinePaint?[rows];
        }
    }

    private void ClearLinePaints()
    {
        for (var i = 0; i < linePaints.Length; i++)
        {
            linePaints[i] = null;
        }
    }

    // ---------------------------------------------------------------- viewport anchor

    /// <summary>
    /// Keeps the scrolled-back viewport and the selection on the text they point at while output keeps arriving.
    /// <see cref="TerminalEmulator.ScrollbackPushed"/> moves the viewport towards the bottom (the scrollback grew by one
    /// line), so an offset above zero is raised by the push delta to stay on the same text; at the bottom the view follows
    /// the output. <see cref="TerminalEmulator.ScrollbackDropped"/> shifts every absolute line index down, so the selection
    /// is moved by the same amount and dropped once it would fall off the oldest line. Call with
    /// <see cref="TerminalViewModel.Gate"/> held.
    /// </summary>
    private void SyncScrollAnchor(TerminalEmulator emulator)
    {
        var pushed = emulator.ScrollbackPushed;
        var dropped = emulator.ScrollbackDropped;
        var pushedDelta = pushed - anchoredPushed;
        var droppedDelta = dropped - anchoredDropped;
        anchoredPushed = pushed;
        anchoredDropped = dropped;

        if (pushedDelta > 0 && scrollOffset > 0)
        {
            scrollOffset = (int)Math.Min(scrollOffset + pushedDelta, emulator.ScrollbackCount);
        }

        if (droppedDelta > 0)
        {
            ShiftSelection(droppedDelta);
        }
    }

    /// <summary>Adopts the emulator's counters as the baseline, so a freshly bound terminal has nothing to compensate.</summary>
    private void ResetScrollAnchor()
    {
        if (Source is { } viewModel)
        {
            lock (viewModel.Gate)
            {
                anchoredPushed = viewModel.Emulator.ScrollbackPushed;
                anchoredDropped = viewModel.Emulator.ScrollbackDropped;
            }

            return;
        }

        anchoredPushed = 0;
        anchoredDropped = 0;
    }

    /// <summary>Moves the selection up by the lines the scrollback dropped; a selection that falls off the oldest line is gone.</summary>
    private void ShiftSelection(long dropped)
    {
        if (!hasSelection)
        {
            return;
        }

        var start = selectionStart.Line - dropped;
        var end = selectionEnd.Line - dropped;
        if (start < 0 || end < 0)
        {
            selectionStart = default;
            selectionEnd = default;
            hasSelection = false;
            selecting = false;
            return;
        }

        selectionStart = ((int)start, selectionStart.Column);
        selectionEnd = ((int)end, selectionEnd.Column);
    }

    // ---------------------------------------------------------------- keyboard and text input

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Source is not { } viewModel)
        {
            base.OnKeyDown(e);
            return;
        }

        // Ctrl+` is the shell's "new terminal" shortcut: leave it to the window's key binding.
        if ((e.Key is Key.OemTilde or Key.Oem3) && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            return;
        }

        if (IsCopyGesture(e))
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (IsPasteGesture(e))
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        var modifiers = ToTerminalModifiers(e.KeyModifiers);
        Span<byte> buffer = stackalloc byte[KeyEncoder.MaxSequenceLength];
        if (TryMapKey(e.Key, out var key))
        {
            TerminalModes modes;
            lock (viewModel.Gate)
            {
                modes = viewModel.Emulator.Modes;
            }

            if (KeyEncoder.TryEncode(key, modifiers, modes, buffer, out var written))
            {
                viewModel.SendInput(buffer[..written]);
                scrollOffset = 0;
                e.Handled = true;
                return;
            }
        }

        // Ctrl/Alt plus a printable key becomes a control code or an ESC-prefixed character, which is how ^C, ^D, ^Z and
        // Alt+B work in a shell. Command (Meta) is the window's shortcut modifier and is never sent as text.
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0 && TryMapRune(e.Key, out var rune))
        {
            Span<byte> encoded = stackalloc byte[8];
            var characterModifiers = modifiers & (TerminalModifiers.Control | TerminalModifiers.Alt | TerminalModifiers.Shift);
            if (KeyEncoder.TryEncodeChar(rune, characterModifiers, encoded, out var length))
            {
                viewModel.SendInput(encoded[..length]);
                scrollOffset = 0;
                e.Handled = true;
            }
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (Source is not { } viewModel || string.IsNullOrEmpty(e.Text))
        {
            base.OnTextInput(e);
            return;
        }

        // Control characters belong to KeyDown (Enter, Tab, Escape, Backspace); sending them twice would duplicate.
        var printable = false;
        foreach (var character in e.Text)
        {
            if (character >= ' ' && character != '\u007f')
            {
                printable = true;
                break;
            }
        }

        if (!printable)
        {
            e.Handled = true;
            return;
        }

        viewModel.SendInput(Encoding.UTF8.GetBytes(e.Text));
        scrollOffset = 0;
        e.Handled = true;
    }

    private static bool IsCopyGesture(KeyEventArgs e) => IsMacOs
        ? e.Key == Key.C && (e.KeyModifiers & KeyModifiers.Meta) != 0
        : e.Key == Key.C && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) ==
        (KeyModifiers.Control | KeyModifiers.Shift);

    private static bool IsPasteGesture(KeyEventArgs e) => IsMacOs
        ? e.Key == Key.V && (e.KeyModifiers & KeyModifiers.Meta) != 0
        : e.Key == Key.V && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) ==
        (KeyModifiers.Control | KeyModifiers.Shift);

    private static bool TryMapKey(Key key, out TerminalKey terminalKey)
    {
        switch (key)
        {
            case Key.Enter:
                terminalKey = TerminalKey.Enter;
                return true;
            case Key.Tab:
                terminalKey = TerminalKey.Tab;
                return true;
            case Key.Back:
                terminalKey = TerminalKey.Backspace;
                return true;
            case Key.Escape:
                terminalKey = TerminalKey.Escape;
                return true;
            case Key.Up:
                terminalKey = TerminalKey.Up;
                return true;
            case Key.Down:
                terminalKey = TerminalKey.Down;
                return true;
            case Key.Left:
                terminalKey = TerminalKey.Left;
                return true;
            case Key.Right:
                terminalKey = TerminalKey.Right;
                return true;
            case Key.Home:
                terminalKey = TerminalKey.Home;
                return true;
            case Key.End:
                terminalKey = TerminalKey.End;
                return true;
            case Key.PageUp:
                terminalKey = TerminalKey.PageUp;
                return true;
            case Key.PageDown:
                terminalKey = TerminalKey.PageDown;
                return true;
            case Key.Insert:
                terminalKey = TerminalKey.Insert;
                return true;
            case Key.Delete:
                terminalKey = TerminalKey.Delete;
                return true;
            case Key.F1:
                terminalKey = TerminalKey.F1;
                return true;
            case Key.F2:
                terminalKey = TerminalKey.F2;
                return true;
            case Key.F3:
                terminalKey = TerminalKey.F3;
                return true;
            case Key.F4:
                terminalKey = TerminalKey.F4;
                return true;
            case Key.F5:
                terminalKey = TerminalKey.F5;
                return true;
            case Key.F6:
                terminalKey = TerminalKey.F6;
                return true;
            case Key.F7:
                terminalKey = TerminalKey.F7;
                return true;
            case Key.F8:
                terminalKey = TerminalKey.F8;
                return true;
            case Key.F9:
                terminalKey = TerminalKey.F9;
                return true;
            case Key.F10:
                terminalKey = TerminalKey.F10;
                return true;
            case Key.F11:
                terminalKey = TerminalKey.F11;
                return true;
            case Key.F12:
                terminalKey = TerminalKey.F12;
                return true;
            default:
                terminalKey = default;
                return false;
        }
    }

    /// <summary>The character a printable key stands for, used for the Ctrl/Alt control-code path.</summary>
    private static bool TryMapRune(Key key, out int rune)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            rune = 'a' + ((int)key - (int)Key.A);
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            rune = '0' + ((int)key - (int)Key.D0);
            return true;
        }

        rune = key switch
        {
            Key.Space => ' ',
            Key.OemTilde or Key.Oem3 => '`',
            Key.OemMinus => '-',
            Key.OemPlus => '=',
            Key.OemOpenBrackets => '[',
            Key.OemCloseBrackets => ']',
            Key.OemPipe => '\\',
            Key.OemSemicolon => ';',
            Key.OemQuotes => '\'',
            Key.OemComma => ',',
            Key.OemPeriod => '.',
            Key.OemQuestion => '/',
            _ => -1,
        };
        return rune >= 0;
    }

    private static TerminalModifiers ToTerminalModifiers(KeyModifiers modifiers)
    {
        var result = TerminalModifiers.None;
        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            result |= TerminalModifiers.Shift;
        }

        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            result |= TerminalModifiers.Alt;
        }

        if ((modifiers & KeyModifiers.Control) != 0)
        {
            result |= TerminalModifiers.Control;
        }

        if ((modifiers & KeyModifiers.Meta) != 0)
        {
            result |= TerminalModifiers.Meta;
        }

        return result;
    }

    // ---------------------------------------------------------------- pointer: selection, mouse reporting, links, wheel

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Source is not { } viewModel)
        {
            return;
        }

        Focus();
        hasMotionCell = false;
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed)
        {
            // The context menu opens on its own; nothing to encode.
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        MouseTracking tracking;
        lock (viewModel.Gate)
        {
            tracking = viewModel.Emulator.MouseMode;
        }

        var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if (tracking != MouseTracking.None && !shift)
        {
            if (SendMouse(viewModel, MouseEventKind.Press, VtMouseButton.Left, point, e.KeyModifiers))
            {
                forwardedPress = true;
                e.Pointer.Capture(this);
                e.Handled = true;
            }

            return;
        }

        if ((e.KeyModifiers & KeyModifiers.Control) != 0 && LinkAt(viewModel, point) is { } link)
        {
            OpenLink(link);
            e.Handled = true;
            return;
        }

        var cell = CellAt(viewModel, point);
        selecting = false;
        forwardedPress = false;
        if (e.ClickCount >= 3)
        {
            SelectLine(viewModel, cell.Line);
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            SelectWord(viewModel, cell.Line, cell.Column);
            e.Handled = true;
            return;
        }

        selectionStart = (cell.Line, cell.Column);
        selectionEnd = selectionStart;
        hasSelection = false;
        selecting = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Source is not { } viewModel)
        {
            return;
        }

        if (forwardedPress)
        {
            // 1002/1003 drags: the program sees the pointer move with the button still down.
            if (SendMouse(viewModel, MouseEventKind.Move, VtMouseButton.Left, e.GetPosition(this), e.KeyModifiers))
            {
                e.Handled = true;
            }

            return;
        }

        if (!selecting)
        {
            if (ReportAnyMotion(viewModel, e))
            {
                e.Handled = true;
            }

            return;
        }

        var cell = CellAt(viewModel, e.GetPosition(this));
        if (cell.Line == selectionEnd.Line && cell.Column == selectionEnd.Column)
        {
            return;
        }

        selectionEnd = (cell.Line, cell.Column);
        hasSelection = true;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Source is not { } viewModel)
        {
            return;
        }

        if (selecting)
        {
            selecting = false;
            var (from, to) = SelectionOrder();
            hasSelection = hasSelection || from != to;
            e.Pointer.Capture(null);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (forwardedPress)
        {
            forwardedPress = false;
            if (SendMouse(viewModel, MouseEventKind.Release, VtMouseButton.Left, e.GetPosition(this), e.KeyModifiers))
            {
                e.Handled = true;
            }

            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        selecting = false;
        forwardedPress = false;
        hasMotionCell = false;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        // Re-entering the cell the pointer left is a move the program has not seen yet.
        hasMotionCell = false;
    }

    /// <summary>
    /// DECSET 1003 reports every pointer movement, including one with no button held. A move inside the cell already
    /// reported is not sent again, so a slow drag does not flood the program with identical positions; Shift keeps the
    /// pointer local, as it does for presses.
    /// </summary>
    private bool ReportAnyMotion(TerminalViewModel viewModel, PointerEventArgs e)
    {
        MouseTracking tracking;
        lock (viewModel.Gate)
        {
            SyncScrollAnchor(viewModel.Emulator);
            tracking = viewModel.Emulator.MouseMode;
        }

        if (tracking != MouseTracking.AnyEvent || (e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            hasMotionCell = false;
            return false;
        }

        var cell = CellAt(viewModel, e.GetPosition(this));
        if (hasMotionCell && motionCell == (cell.Column, cell.Row))
        {
            return false;
        }

        motionCell = (cell.Column, cell.Row);
        hasMotionCell = true;
        return SendMouse(viewModel, MouseEventKind.Move, VtMouseButton.None, e.GetPosition(this), e.KeyModifiers);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Source is not { } viewModel || e.Delta.Y == 0)
        {
            return;
        }

        var notches = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta.Y)));
        var up = e.Delta.Y > 0;
        MouseTracking tracking;
        bool sgr;
        TerminalModes modes;
        bool alternate;
        int scrollback;
        lock (viewModel.Gate)
        {
            // Output received since the last frame moves the viewport: compensate before adding the wheel's own notches.
            SyncScrollAnchor(viewModel.Emulator);
            tracking = viewModel.Emulator.MouseMode;
            sgr = viewModel.Emulator.MouseSgr;
            modes = viewModel.Emulator.Modes;
            alternate = viewModel.Emulator.IsAlternateScreen;
            scrollback = viewModel.Emulator.ScrollbackCount;
        }

        if (tracking != MouseTracking.None)
        {
            var cell = CellAt(viewModel, e.GetPosition(this));
            var modifiers = ToTerminalModifiers(e.KeyModifiers);
            Span<byte> buffer = stackalloc byte[MouseEncoder.MaxSequenceLength];
            for (var i = 0; i < notches; i++)
            {
                if (MouseEncoder.TryEncode(
                        MouseEventKind.Press,
                        up ? VtMouseButton.WheelUp : VtMouseButton.WheelDown,
                        cell.Column,
                        cell.Row,
                        modifiers,
                        tracking,
                        sgr,
                        buffer,
                        out var written))
                {
                    viewModel.SendInput(buffer[..written]);
                }
            }

            e.Handled = true;
            return;
        }

        if (alternate)
        {
            // A full-screen program owns the screen: the wheel becomes arrow keys, as xterm does in the alternate buffer.
            Span<byte> buffer = stackalloc byte[KeyEncoder.MaxSequenceLength];
            for (var i = 0; i < notches; i++)
            {
                if (KeyEncoder.TryEncode(
                        up ? TerminalKey.Up : TerminalKey.Down,
                        TerminalModifiers.None,
                        modes,
                        buffer,
                        out var written))
                {
                    viewModel.SendInput(buffer[..written]);
                }
            }

            e.Handled = true;
            return;
        }

        var target = scrollOffset + (up ? notches : -notches) * WheelScrollLines;
        scrollOffset = Math.Clamp(target, 0, scrollback);
        InvalidateVisual();
        e.Handled = true;
    }

    private bool SendMouse(
        TerminalViewModel viewModel,
        MouseEventKind kind,
        VtMouseButton button,
        Point point,
        KeyModifiers modifiers)
    {
        MouseTracking tracking;
        bool sgr;
        lock (viewModel.Gate)
        {
            tracking = viewModel.Emulator.MouseMode;
            sgr = viewModel.Emulator.MouseSgr;
        }

        if (tracking == MouseTracking.None)
        {
            return false;
        }

        var cell = CellAt(viewModel, point);
        Span<byte> buffer = stackalloc byte[MouseEncoder.MaxSequenceLength];
        if (!MouseEncoder.TryEncode(
                kind,
                button,
                cell.Column,
                cell.Row,
                ToTerminalModifiers(modifiers),
                tracking,
                sgr,
                buffer,
                out var written))
        {
            return false;
        }

        viewModel.SendInput(buffer[..written]);
        return true;
    }

    private (int Line, int Row, int Column) CellAt(TerminalViewModel viewModel, Point point)
    {
        int scrollback;
        int columns;
        int total;
        lock (viewModel.Gate)
        {
            // The row under the pointer only matches what is on screen once pending output has been compensated.
            SyncScrollAnchor(viewModel.Emulator);
            scrollback = viewModel.Emulator.ScrollbackCount;
            columns = viewModel.Emulator.Columns;
            total = viewModel.Emulator.TotalLines;
        }

        var offset = Math.Clamp(scrollOffset, 0, scrollback);
        var topLine = scrollback - offset;
        var row = Math.Max(0, (int)Math.Floor(point.Y / cellHeight));
        var column = Math.Max(0, (int)Math.Floor(point.X / cellWidth));
        var line = topLine + row;
        return (
            Math.Clamp(line, 0, Math.Max(0, total - 1)),
            row,
            Math.Clamp(column, 0, Math.Max(0, columns - 1)));
    }

    /// <summary>The selection as an ordered pair of positions, whichever direction the pointer dragged.</summary>
    private ((int Line, int Column) From, (int Line, int Column) To) SelectionOrder() =>
        selectionStart.Line < selectionEnd.Line ||
        (selectionStart.Line == selectionEnd.Line && selectionStart.Column <= selectionEnd.Column)
            ? (selectionStart, selectionEnd)
            : (selectionEnd, selectionStart);

    private void SelectWord(TerminalViewModel viewModel, int line, int column)
    {
        int start;
        int end;
        lock (viewModel.Gate)
        {
            (start, end) = TerminalText.WordAt(viewModel.Emulator, line, column);
        }

        selectionStart = (line, start);
        selectionEnd = (line, end);
        hasSelection = true;
        InvalidateVisual();
    }

    private void SelectLine(TerminalViewModel viewModel, int line)
    {
        int last;
        lock (viewModel.Gate)
        {
            last = Math.Max(0, viewModel.Emulator.Columns - 1);
        }

        selectionStart = (line, 0);
        selectionEnd = (line, last);
        hasSelection = true;
        InvalidateVisual();
    }

    private string? LinkAt(TerminalViewModel viewModel, Point point)
    {
        var cell = CellAt(viewModel, point);
        string? link = null;
        lock (viewModel.Gate)
        {
            var emulator = viewModel.Emulator;
            if (cell.Line >= emulator.TotalLines)
            {
                return null;
            }

            var target = emulator.GetLine(cell.Line)[cell.Column];
            if (target.HyperlinkId != 0)
            {
                link = emulator.GetHyperlink(target.HyperlinkId);
            }
        }

        return link;
    }

    private static void OpenLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            // Only web links are opened: a file:// or custom-scheme link from a program is not a user gesture.
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Trace.WriteLine("[terminal] the link could not be opened: " + exception.Message);
        }
    }

    // ---------------------------------------------------------------- clipboard and local commands

    private void CopySelection()
    {
        if (!hasSelection || Source is not { } viewModel)
        {
            return;
        }

        string text;
        lock (viewModel.Gate)
        {
            // The cached selection is anchored to the text, so copy what a repaint would highlight.
            SyncScrollAnchor(viewModel.Emulator);
            var (from, to) = SelectionOrder();
            text = TerminalText.GetText(viewModel.Emulator, from, to);
        }

        if (text.Length == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        _ = CopyAsync(clipboard, text);
    }

    private static async Task CopyAsync(IClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[terminal] the selection could not be copied: " + exception.Message);
        }
    }

    private async Task PasteAsync()
    {
        if (Source is not { } viewModel || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Trace.WriteLine("[terminal] the clipboard could not be read: " + exception.Message);
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // A pasted newline is Enter, not a literal line feed: without this a multi-line paste into a shell does nothing.
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');

        // Escape and C1 control characters are dropped from a paste in both modes: a clipboard holding "ESC [ 201 ~" would
        // otherwise end the bracketed region early and the rest of the text would run as typed commands (xterm, VTE and
        // kitty strip ESC for the same reason).
        text = StripEscapes(text);

        bool bracketed;
        lock (viewModel.Gate)
        {
            bracketed = (viewModel.Emulator.Modes & TerminalModes.BracketedPaste) != 0;
        }

        var payload = bracketed ? "\u001b[200~" + text + "\u001b[201~" : text;
        viewModel.SendInput(Encoding.UTF8.GetBytes(payload));
        scrollOffset = 0;
        InvalidateVisual();
    }

    /// <summary>Drops ESC and the C1 controls (U+0080–U+009F); everything else, newlines and tabs included, is pasted as is.</summary>
    private static string StripEscapes(string text)
    {
        var dropped = 0;
        foreach (var character in text)
        {
            if (IsEscape(character))
            {
                dropped++;
            }
        }

        if (dropped == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length - dropped);
        foreach (var character in text)
        {
            if (!IsEscape(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static bool IsEscape(char character) => character == '\u001b' || character is >= '\u0080' and <= '\u009f';

    /// <summary>Clears the screen and the scrollback locally, through the parser, so the emulator stays the single truth.</summary>
    private void ClearScreen()
    {
        if (Source is not { } viewModel)
        {
            return;
        }

        lock (viewModel.Gate)
        {
            viewModel.Emulator.Feed("\u001b[2J\u001b[H"u8);
            viewModel.Emulator.ClearScrollback();
        }

        scrollOffset = 0;
        hasSelection = false;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- cached paint data

    private sealed class LinePaint(TerminalLine line, long version, bool reverse, List<LineRun> runs)
    {
        public TerminalLine Line { get; } = line;

        public long Version { get; } = version;

        /// <summary>DECSCNM swaps the whole screen at paint time, so a reverse-video flip invalidates every cached row.</summary>
        public bool Reverse { get; } = reverse;

        public List<LineRun> Runs { get; } = runs;
    }

    private sealed record LineRun(int StartColumn, FormattedText Text);
}
