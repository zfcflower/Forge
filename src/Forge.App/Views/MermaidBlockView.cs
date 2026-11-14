using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Forge.App.Changes.Markdown;
using Forge.App.Localization;
using Forge.App.Logging;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.App.Workbench.Controls;
using ZLogger;

namespace Forge.App.Views;

/// <summary>
/// One fenced Mermaid block (wave W11): the same header and source view as an ordinary code block, plus the diagram itself.
/// <para>The block renders only after its fence closed (<see cref="MdBlock.FenceClosed"/>), so a streaming reply never parses
/// a half-written diagram; while it streams it shows the source, which is what the user would see anyway. A type outside the
/// configured whitelist stays an ordinary code block - the whitelist is a display decision, not an error. A render that fails
/// keeps the source visible, says why (with the line when the message names one) and offers a retry and a one-click hand-off
/// to the model, so a bad diagram is fixed in the conversation instead of leaving a blank box.</para>
/// </summary>
internal sealed class MermaidBlockView : Border, ICopyableText
{
    private readonly MarkdownCodeHeader header;
    private readonly MarkdownCodeTextView sourceView;
    private readonly MermaidDiagramView diagramView;
    private readonly StackPanel statusRow;
    private readonly FluentIcon statusIcon;
    private readonly TextBlock statusText;
    private readonly SelectableTextBlock detailText;
    private readonly Button toggleButton;
    private readonly Button retryButton;
    private readonly Button fixButton;
    private readonly TextBlock fixStatus;
    private readonly StackPanel actions;
    private readonly FluentIcon toggleIcon;

    private string source;
    private string info;
    private bool closed;
    private bool attached;
    private bool showingSource;
    private bool handingOff;
    private State state;
    private MermaidRenderResult? outcome;
    private CancellationTokenSource? pending;
    private int generation;
    private string paletteKey = string.Empty;
    private TimeSpan requestTimeout = MermaidSettings.Timeout();
    private bool? statusIconFailed;
    private LocText fixStatusText;
    private IDisposable? statusIconData;
    private IDisposable? statusIconBrush;
    private IDisposable? toggleIconData;

    protected override Type StyleKeyOverride => typeof(Border);

    private enum State
    {
        /// <summary>The fence is still open: the source is shown and nothing is rendered.</summary>
        Streaming,

        /// <summary>Closed and waiting for the background render.</summary>
        Rendering,

        /// <summary>A picture is available.</summary>
        Rendered,

        /// <summary>The render failed; <see cref="outcome"/> says why.</summary>
        Failed,

        /// <summary>The diagram type is not in the whitelist: an ordinary code block.</summary>
        Disabled,
    }

    public MermaidBlockView(string source, string info, bool closed)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(info);

        Classes.Add("md-code");
        this.source = source;
        this.info = info;
        this.closed = closed;
        state = closed ? State.Rendering : State.Streaming;

        toggleIcon = new FluentIcon { Width = 12, Height = 12 };
        toggleButton = new Button
        {
            Content = toggleIcon,
            Width = 26,
            Height = 22,
            IsVisible = false,
        };
        toggleButton.Classes.Add("ghost");
        toggleButton.Classes.Add("icon");
        toggleButton.Click += OnToggleSource;
        AutomationProperties.SetAutomationId(toggleButton, "mermaid-toggle-source");

        header = new MarkdownCodeHeader(this, toggleButton);
        header.SetLabel(info);

        sourceView = new MarkdownCodeTextView(source);
        diagramView = new MermaidDiagramView { IsVisible = false };
        AutomationProperties.SetAutomationId(diagramView, "mermaid-diagram");

        statusIcon = new FluentIcon { Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
        statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        statusText.Classes.Add("mermaid-status");
        statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, IsVisible = false };
        statusRow.Children.Add(statusIcon);
        statusRow.Children.Add(statusText);
        AutomationProperties.SetAutomationId(statusRow, "mermaid-status");

        detailText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        detailText.Classes.Add("mermaid-detail");

        retryButton = ActionButton("mermaid-retry", MermaidStrings.Retry, OnRetry);
        fixButton = ActionButton("mermaid-fix", MermaidStrings.Fix, OnFix);
        fixStatus = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        fixStatus.Classes.Add("mermaid-fix-status");
        fixStatus.IsVisible = false;
        actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
        actions.Children.Add(retryButton);
        actions.Children.Add(fixButton);
        actions.Children.Add(fixStatus);

        var content = new Panel();
        content.Children.Add(sourceView);
        content.Children.Add(diagramView);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(statusRow);
        body.Children.Add(detailText);
        body.Children.Add(content);
        body.Children.Add(actions);

        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(header);
        stack.Children.Add(body);
        Child = stack;

        ApplyName();
        ApplyState();
    }

    /// <summary>The block's current source, read by the header's copy button when it is pressed.</summary>
    public string CopyText => source;

    /// <summary>
    /// Replaces the source in place (a streaming block grows without being rebuilt). A fence that just closed starts the
    /// render; the text of an already closed block that changed is rendered again.
    /// </summary>
    public void Update(string source, string info, bool closed)
    {
        var wasClosed = this.closed;
        var changed = !string.Equals(this.source, source, StringComparison.Ordinal) ||
            !string.Equals(this.info, info, StringComparison.Ordinal);
        this.source = source;
        this.info = info;
        this.closed = closed;
        sourceView.Text = source;
        header.SetLabel(info);
        ApplyName();

        if (!closed)
        {
            // A streaming block is rebuilt on every frame: the text is already updated above, and nothing else about an
            // open-fence block changes between frames, so skip the state pass (and its resource re-binds).
            if (state != State.Streaming)
            {
                CancelPending();
                state = State.Streaming;
                ApplyState();
            }

            return;
        }

        if (!wasClosed || changed)
        {
            if (attached)
            {
                StartRender();
            }
            else
            {
                state = State.Rendering;
                ApplyState();
            }
        }
    }

    /// <summary>
    /// Starts rendering once the block is in a tree: the palette comes from the theme tokens, and a control built before the
    /// transcript attaches must not freeze the fallback colours into its picture.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        ActualThemeVariantChanged += OnThemeVariantChanged;
        // A block built before it was added to the tree (or one whose render was cancelled on detach) starts here, where the
        // theme tokens are reachable; one that already rendered while attached only re-applies its state.
        if (closed && (state == State.Streaming || (state == State.Rendering && pending is null)))
        {
            StartRender();
            return;
        }

        // The theme may have changed while detached (no ActualThemeVariantChanged reached this block): a picture drawn with
        // the old palette is replaced, keeping it on screen until the new one is ready.
        if (closed && state is State.Rendered or State.Failed &&
            !string.Equals(MermaidPalette.FromTheme(this).CacheKey, paletteKey, StringComparison.Ordinal))
        {
            StartRender(keepDiagram: true);
            return;
        }

        ApplyState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        ActualThemeVariantChanged -= OnThemeVariantChanged;
        CancelPending();
        base.OnDetachedFromVisualTree(e);
    }

    /// <param name="keepDiagram">
    /// A theme switch re-renders the same source: keep the current picture on screen until the new one is ready instead of
    /// flashing the source text for the length of a render.
    /// </param>
    private void StartRender(bool keepDiagram = false)
    {
        CancelPending();
        if (!keepDiagram)
        {
            detailText.IsVisible = false;
            fixStatus.IsVisible = false;
            fixStatusText = default;
            showingSource = true;
        }

        var enabled = MermaidSettings.EnabledTypes();
        requestTimeout = MermaidSettings.Timeout();
        var request = new MermaidRenderRequest(source, MermaidPalette.FromTheme(this), enabled, requestTimeout);
        if (MermaidDiagramTypes.Detect(source) is { } kind && (kind & enabled) == 0)
        {
            CancelPending();
            outcome = null;
            state = State.Disabled;
            ApplyState();
            return;
        }

        paletteKey = request.Palette.CacheKey;
        outcome = null;
        if (!keepDiagram)
        {
            state = State.Rendering;
            ApplyState();
        }

        var cts = new CancellationTokenSource();
        pending = cts;
        _ = RunAsync(request, ++generation, cts.Token);
    }

    private async Task RunAsync(MermaidRenderRequest request, int number, CancellationToken cancellation)
    {
        MermaidRenderResult result;
        try
        {
            result = await MermaidRenderService.Current.RenderAsync(request, cancellation).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = MermaidRenderResult.Failed(MermaidRenderFailure.Internal, exception.Message);
        }

        if (cancellation.IsCancellationRequested || number != generation)
        {
            return;
        }

        outcome = result;
        if (result.Failure == MermaidRenderFailure.Disabled && result.Outcome == MermaidRenderOutcome.Failed)
        {
            state = State.Disabled;
            ApplyState();
            return;
        }

        if (result.Outcome == MermaidRenderOutcome.Rendered)
        {
            diagramView.SetPicture(result.Picture);
            state = State.Rendered;
            showingSource = false;
        }
        else
        {
            state = State.Failed;
            showingSource = true;
        }

        ApplyState();
    }

    private void ShowFailure(MermaidRenderResult result)
    {
        statusText.Text = FailureTitle(result);
        detailText.Text = FailureDetail(result);
        detailText.IsVisible = detailText.Text.Length > 0;
    }

    private void ApplyState()
    {
        var failed = state == State.Failed;
        statusText.Classes.Set("mermaid-status-error", failed);
        if (statusIconFailed != failed)
        {
            statusIconFailed = failed;
            SetStatusIcon(failed ? "IconClose" : "IconSparkle", failed ? "DangerBrush" : "TextMutedBrush");
        }

        statusRow.IsVisible = state is State.Rendering or State.Failed;
        switch (state)
        {
            case State.Streaming:
            case State.Disabled:
                statusText.Text = string.Empty;
                statusRow.IsVisible = false;
                detailText.IsVisible = false;
                sourceView.IsVisible = true;
                diagramView.IsVisible = false;
                actions.IsVisible = false;
                toggleButton.IsVisible = false;
                break;

            case State.Rendering:
                statusText.Text = Loc.Instance[MermaidStrings.Rendering];
                detailText.IsVisible = false;
                sourceView.IsVisible = true;
                diagramView.IsVisible = false;
                actions.IsVisible = false;
                toggleButton.IsVisible = false;
                break;

            case State.Rendered:
                statusRow.IsVisible = false;
                detailText.IsVisible = false;
                sourceView.IsVisible = showingSource;
                diagramView.IsVisible = !showingSource;
                actions.IsVisible = false;
                toggleButton.IsVisible = true;
                UpdateToggle();
                break;

            case State.Failed:
                ShowFailure(outcome ?? MermaidRenderResult.Failed(MermaidRenderFailure.Internal));
                sourceView.IsVisible = true;
                diagramView.IsVisible = false;
                actions.IsVisible = true;
                toggleButton.IsVisible = false;
                UpdateActions();
                break;
        }

        ApplyName();
    }

    /// <summary>The retry button is always offered; the hand-off only when a session can take it, which is decided by the tree.</summary>
    private void UpdateActions()
    {
        var retry = Loc.Instance[MermaidStrings.Retry];
        var fix = Loc.Instance[MermaidStrings.Fix];
        retryButton.Content = retry;
        AutomationProperties.SetName(retryButton, retry);
        fixButton.Content = fix;
        AutomationProperties.SetName(fixButton, fix);
        var host = MermaidFixHost.Resolve(this);
        fixButton.IsVisible = host is { CanHandOff: true };
        fixButton.IsEnabled = !handingOff;
        retryButton.IsEnabled = !handingOff;
        if (fixStatus.IsVisible && !fixStatusText.IsEmpty)
        {
            fixStatus.Text = fixStatusText.Resolve();
        }
    }

    private void UpdateToggle()
    {
        var showing = showingSource;
        toggleIconData?.Dispose();
        toggleIconData = toggleIcon.Bind(
            PathIcon.DataProperty,
            this.GetResourceObservable(showing ? "IconEye" : "IconChangesDocument"));
        var label = Loc.Instance[showing ? MermaidStrings.ShowDiagram : MermaidStrings.ShowSource];
        ToolTip.SetTip(toggleButton, label);
        AutomationProperties.SetName(toggleButton, label);
    }

    /// <summary>Rebinds the status glyph and its brush, dropping the previous subscriptions (one per state change, not per frame).</summary>
    private void SetStatusIcon(string dataKey, string brushKey)
    {
        statusIconData?.Dispose();
        statusIconData = statusIcon.Bind(PathIcon.DataProperty, this.GetResourceObservable(dataKey));
        statusIconBrush?.Dispose();
        statusIconBrush = statusIcon.Bind(PathIcon.ForegroundProperty, this.GetResourceObservable(brushKey));
    }

    private void OnToggleSource(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (state != State.Rendered)
        {
            return;
        }

        showingSource = !showingSource;
        sourceView.IsVisible = showingSource;
        diagramView.IsVisible = !showingSource;
        UpdateToggle();
    }

    private void OnRetry(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (state is State.Rendering)
        {
            return;
        }

        StartRender();
    }

    private void OnFix(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _ = HandOffAsync();

    /// <summary>
    /// Hands the broken diagram to the session that owns this transcript (the nearest one in the tree), or to the active
    /// session when the block is in a document preview. A busy session takes the message into its prompt box instead of
    /// starting a second turn behind the user's back; the label says which happened.
    /// </summary>
    private async Task HandOffAsync()
    {
        if (handingOff || state != State.Failed)
        {
            return;
        }

        var host = MermaidFixHost.Resolve(this);
        if (host is not { CanHandOff: true })
        {
            fixStatusText = new LocText(MermaidStrings.RenderFailed);
            fixStatus.Text = fixStatusText.Resolve();
            fixStatus.IsVisible = true;
            return;
        }

        handingOff = true;
        fixStatusText = new LocText(MermaidStrings.FixSending);
        fixStatus.Text = fixStatusText.Resolve();
        fixStatus.IsVisible = true;
        UpdateActions();
        try
        {
            var ran = await host
                .HandMessageAsync(MermaidFixMessage.Build(source, FailureForModel()), new LocText(MermaidStrings.FixOrigin))
                .ConfigureAwait(true);
            if (!attached)
            {
                return;
            }

            fixStatusText = new LocText(ran ? MermaidStrings.FixSent : MermaidStrings.FixQueued);
            fixStatus.Text = fixStatusText.Resolve();
            fixStatus.IsVisible = true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Markdown.ZLogWarning($"mermaid hand-off failed: {exception.Message}");
            fixStatusText = new LocText(MermaidStrings.RenderFailed);
            fixStatus.Text = fixStatusText.Resolve();
            fixStatus.IsVisible = true;
        }
        finally
        {
            handingOff = false;
            if (attached)
            {
                UpdateActions();
            }
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (attached)
        {
            ApplyState();
        }
    }

    /// <summary>
    /// Re-renders for the new theme: the picture carries concrete colours, so a variant switch has to produce a new one.
    /// A variant change that leaves the palette identical (a custom theme) does not render again.
    /// </summary>
    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        if (!attached || !closed || state is not (State.Rendered or State.Failed))
        {
            return;
        }

        if (string.Equals(MermaidPalette.FromTheme(this).CacheKey, paletteKey, StringComparison.Ordinal))
        {
            return;
        }

        StartRender(keepDiagram: true);
    }

    private void CancelPending()
    {
        var cts = pending;
        pending = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
        generation++;
    }

    private string FailureTitle(MermaidRenderResult result) => result.Failure switch
    {
        MermaidRenderFailure.Timeout => Loc.Instance.Format(
            MermaidStrings.Timeout,
            TextFormat.Number((long)requestTimeout.TotalMilliseconds)),
        MermaidRenderFailure.TooLarge => Loc.Instance[MermaidStrings.TooLarge],
        MermaidRenderFailure.Parse => Loc.Instance[MermaidStrings.RenderFailed],
        _ => Loc.Instance[MermaidStrings.Internal],
    };

    /// <summary>The renderer's own message, with the line it points at when one is known.</summary>
    private string FailureDetail(MermaidRenderResult result)
    {
        var error = result.Error;
        if (string.IsNullOrWhiteSpace(error))
        {
            return string.Empty;
        }

        return result.ErrorLine is { } line && line > 0
            ? error.Trim() + " (" + Loc.Instance.Format(MermaidStrings.Line, TextFormat.Number(line)) + ")"
            : error.Trim();
    }

    /// <summary>What the model receives: the title (localized, so the instruction is clear) followed by the renderer's message.</summary>
    private string FailureForModel()
    {
        var result = outcome ?? MermaidRenderResult.Failed(MermaidRenderFailure.Internal);
        var detail = FailureDetail(result);
        return detail.Length > 0 ? FailureTitle(result) + " — " + detail : FailureTitle(result);
    }

    private void ApplyName()
    {
        AutomationProperties.SetName(this, info.Length > 0 ? "mermaid" : null);
        AutomationProperties.SetName(diagramView, Loc.Instance[MermaidStrings.Diagram]);
    }

    private static Button ActionButton(string automationId, string textKey, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
    {
        var button = new Button { Content = Loc.Instance[textKey] };
        button.Classes.Add("ghost");
        button.Classes.Add("mermaid-action");
        AutomationProperties.SetAutomationId(button, automationId);
        AutomationProperties.SetName(button, Loc.Instance[textKey]);
        button.Click += click;
        return button;
    }
}

/// <summary>
/// Finds the session a Mermaid block belongs to. A chat reply resolves the nearest <see cref="SessionView"/> - an inactive tab
/// must send its fix to its own session, not to whatever happens to be active - and a block in a document preview (the diff
/// view) falls back to the window's active session, which is the one working on that repository. Nothing in the tree widens
/// the <see cref="IAgentMessageHost"/> contract, and a host without a session simply hides the button.
/// </summary>
internal static class MermaidFixHost
{
    /// <summary>
    /// A test seam: a unit test hosts a block outside a real session and hands it a fake host. Production never sets it, and the
    /// tree resolution below is what the app uses.
    /// </summary>
    internal static Func<Visual, IAgentMessageHost?>? ResolveOverride { get; set; }

    internal static IAgentMessageHost? Resolve(Visual element)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (ResolveOverride is { } resolve)
        {
            return resolve(element);
        }

        // A block inside a session's transcript belongs to that session only: when it cannot take the hand-off the block has
        // no host, it must not fall back to whichever session happens to be active.
        if (element.FindAncestorOfType<SessionView>() is { } sessionView)
        {
            return sessionView.DataContext is IAgentMessageHost { CanHandOff: true } owner ? owner : null;
        }

        return TopLevel.GetTopLevel(element)?.DataContext is MainWindowViewModel { ActiveSession: { } active } && active.CanHandOff
            ? active
            : null;
    }
}
