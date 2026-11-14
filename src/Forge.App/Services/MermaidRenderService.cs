using System.Text.RegularExpressions;
using Mermaider;
using Mermaider.Models;
using SkiaSharp;
using Svg.Skia;
using MermaidKinds = Mermaider.Models.DiagramTypes;

namespace Forge.App.Services;

/// <summary>Why a Mermaid diagram could not be drawn; the view turns each into one localized line.</summary>
internal enum MermaidRenderFailure
{
    /// <summary>A syntax or header error, or a type Mermaider rejects; <see cref="MermaidRenderResult.Error"/> carries its text.</summary>
    Parse,

    /// <summary>The render did not finish inside the configured timeout.</summary>
    Timeout,

    /// <summary>The source (or the diagram it declares) exceeded a hard bound, so it was not rendered at all.</summary>
    TooLarge,

    /// <summary>Anything else: an SVG the rasterizer could not read, an unresolved colour, an unexpected exception.</summary>
    Internal,
}

/// <summary>What one render produced: a drawable picture, or the failure the block shows next to its source.</summary>
internal sealed record MermaidRenderResult(
    MermaidRenderOutcome Outcome,
    MermaidPicture? Picture = null,
    MermaidRenderFailure Failure = MermaidRenderFailure.Internal,
    string? Error = null,
    int? ErrorLine = null)
{
    internal static MermaidRenderResult Rendered(MermaidPicture picture) => new(MermaidRenderOutcome.Rendered, picture);

    internal static MermaidRenderResult Failed(MermaidRenderFailure failure, string? error = null, int? line = null) =>
        new(MermaidRenderOutcome.Failed, Failure: failure, Error: error, ErrorLine: line);
}

internal enum MermaidRenderOutcome
{
    Rendered,
    Failed,
}

/// <summary>One render request. The palette is part of it because the same diagram is painted differently per theme.</summary>
internal readonly record struct MermaidRenderRequest(
    string Source,
    MermaidPalette Palette,
    MermaidKinds AllowedTypes,
    TimeSpan Timeout);

/// <summary>
/// A parsed, drawable diagram. Holds the <see cref="SKSvg"/> that owns the <see cref="SKPicture"/> - taking the picture and
/// disposing the SKSvg leaves a dangling handle (a native access violation on the next draw), so both stay together.
/// <para>Nothing disposes this explicitly: an <see cref="SKPicture"/> is ~2 KB, the cache bounds how many exist, and disposal
/// would race the render thread, which may still be replaying the picture of a frame that was queued before the UI replaced
/// it. SkiaSharp's finalizer releases the native picture once no control, drawing operation or cache entry references this.</para>
/// </summary>
internal sealed class MermaidPicture
{
    /// <summary>The object that owns the picture's native handle; kept alive so the handle outlives every replay of it.</summary>
    private readonly SKSvg owner;

    internal MermaidPicture(SKSvg owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        this.owner = owner;
        if (owner.Picture is null)
        {
            throw new InvalidOperationException("the SVG produced an empty picture");
        }
    }

    /// <summary>Read through the owner, so the handle and its owner can never be separated.</summary>
    internal SKPicture Picture => owner.Picture!;

    /// <summary>The diagram's natural size in device-independent pixels, as Mermaider laid it out.</summary>
    internal SKSize Size => new(Picture.CullRect.Width, Picture.CullRect.Height);
}

/// <summary>
/// Renders Mermaid sources off the UI thread and caches the pictures (wave W11). The same reply is rebuilt once when it
/// completes and every block asks again, so a render is keyed by source + palette + whitelist and shared between callers -
/// including two blocks that ask before the first one finished. At most <see cref="MaxCacheEntries"/> pictures are kept and at
/// most <see cref="MaxConcurrentRenders"/> renders run at once: one reply can carry ten diagrams, and they must not all
/// compile together on the thread pool.
/// </summary>
internal sealed partial class MermaidRenderService
{
    /// <summary>Sources longer than this are not rendered: they are a wall of text, not a diagram, and the timeout would be spent parsing it.</summary>
    internal const int MaxSourceLength = 64 * 1024;

    internal const int MaxCacheEntries = 24;

    internal const int MaxConcurrentRenders = 2;

    /// <summary>The service the views use; replaced by tests and by hosts that want their own cache.</summary>
    internal static MermaidRenderService Current { get; set; } = new();

    private readonly Lock cacheLock = new();
    private readonly Dictionary<CacheKey, Lazy<Task<MermaidRenderResult>>> cache = [];

    /// <summary>Insertion order with O(1) removal, so a forgotten (failed) key does not leave a stale slot behind.</summary>
    private readonly LinkedList<CacheKey> order = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheKey>> positions = [];
    private readonly SemaphoreSlim gate = new(MaxConcurrentRenders, MaxConcurrentRenders);

    /// <summary>
    /// The picture for <paramref name="request"/>, produced in the background. Never throws for a render problem - those come
    /// back as <see cref="MermaidRenderOutcome.Failed"/> - and only cancels when <paramref name="cancellation"/> does, which
    /// stops the wait, not the shared render (another block may be waiting for it).
    /// </summary>
    internal async Task<MermaidRenderResult> RenderAsync(MermaidRenderRequest request, CancellationToken cancellation)
    {
        if (request.Source.Length > MaxSourceLength)
        {
            return MermaidRenderResult.Failed(MermaidRenderFailure.TooLarge);
        }

        var key = new CacheKey(request.Source, request.Palette.CacheKey, request.AllowedTypes);
        var entry = Entry(key, request);
        var result = await entry.Value.WaitAsync(cancellation).ConfigureAwait(false);
        if (result.Outcome == MermaidRenderOutcome.Failed)
        {
            // A failure is cheap to compute and may be transient (a timeout); cache only what a retry would reproduce.
            Forget(key, entry);
        }

        return result;
    }

    /// <summary>Drops every cached picture (tests and a theme purge); renders that are still running finish into an empty cache.</summary>
    internal void ClearCache()
    {
        lock (cacheLock)
        {
            cache.Clear();
            order.Clear();
            positions.Clear();
        }
    }

    private Lazy<Task<MermaidRenderResult>> Entry(CacheKey key, MermaidRenderRequest request)
    {
        lock (cacheLock)
        {
            if (cache.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var entry = new Lazy<Task<MermaidRenderResult>>(() => ProduceAsync(request));
            cache[key] = entry;
            positions[key] = order.AddLast(key);
            while (order.Count > MaxCacheEntries)
            {
                var oldest = order.First!.Value;
                order.RemoveFirst();
                positions.Remove(oldest);
                cache.Remove(oldest);
            }

            return entry;
        }
    }

    private void Forget(CacheKey key, Lazy<Task<MermaidRenderResult>> entry)
    {
        lock (cacheLock)
        {
            if (cache.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                cache.Remove(key);
                if (positions.Remove(key, out var node))
                {
                    order.Remove(node);
                }
            }
        }
    }

    /// <summary>Runs one render with the concurrency gate and the wall-clock timeout; the gate is held only for the render it bounds.</summary>
    private async Task<MermaidRenderResult> ProduceAsync(MermaidRenderRequest request)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // RenderCore catches its own failures, so this task cannot fault and a timeout leaves nothing unobserved.
            var work = Task.Run(() => RenderCore(request));
            try
            {
                return await work.WaitAsync(request.Timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The abandoned render keeps the pool busy until Mermaider's own deadline stops it; the slot is released so
                // a hung diagram cannot starve every other diagram of a slot.
                return MermaidRenderResult.Failed(MermaidRenderFailure.Timeout);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Nothing between the gate and RenderCore is expected to throw; a failure here must still be a result the
                // block can show, not a faulted cache entry every retry would rethrow.
                return MermaidRenderResult.Failed(MermaidRenderFailure.Internal, exception.Message);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static MermaidRenderResult RenderCore(MermaidRenderRequest request)
    {
        try
        {
            var svg = MermaidRenderer.RenderSvg(request.Source, new RenderOptions
            {
                Bg = request.Palette.Background,
                Fg = request.Palette.Foreground,
                Accent = request.Palette.Accent,
                Muted = request.Palette.Muted,
                Surface = request.Palette.Surface,
                Border = request.Palette.Border,
                Line = request.Palette.Line,
                // The chat surface is the background; painting it again would square off the block's rounded corner.
                Transparent = true,
                // Flat fills keep the SVG free of the gradient stops whose color-mix() Svg.Skia cannot resolve; the shapes
                // still carry the palette's tints (see MermaidSvgColors).
                Gradient = false,
                Elevation = 0,
                AllowedDiagrams = request.AllowedTypes,
                Limits = ResourceLimits.Default with { RenderDeadline = request.Timeout },
            });

            if (!MermaidSvgColors.TryResolve(svg, request.Palette, out var concrete, out var problem))
            {
                return MermaidRenderResult.Failed(MermaidRenderFailure.Internal, problem);
            }

            var skSvg = SKSvg.CreateFromSvg(concrete);
            if (skSvg.Picture is not { } picture || !(picture.CullRect.Width > 0) || !(picture.CullRect.Height > 0))
            {
                return MermaidRenderResult.Failed(MermaidRenderFailure.Internal, "the SVG produced an empty picture");
            }

            return MermaidRenderResult.Rendered(new MermaidPicture(skSvg));
        }
        catch (MermaidResourceLimitException limit)
        {
            return limit.LimitName.Contains("deadline", StringComparison.OrdinalIgnoreCase)
                ? MermaidRenderResult.Failed(MermaidRenderFailure.Timeout)
                : MermaidRenderResult.Failed(MermaidRenderFailure.TooLarge, limit.Message);
        }
        catch (MermaidParseException parse)
        {
            return MermaidRenderResult.Failed(MermaidRenderFailure.Parse, parse.Message, ErrorLine(parse.Message));
        }
        catch (MermaidSvgException sanitized)
        {
            return MermaidRenderResult.Failed(MermaidRenderFailure.Internal, sanitized.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return MermaidRenderResult.Failed(MermaidRenderFailure.Internal, exception.Message);
        }
    }

    /// <summary>
    /// The source line an error points at, when the message names one. Mermaider 0.15 has no line numbers in its exceptions,
    /// so a header error (the only failure it reports for ordinary Mermaid) is line 1 and everything else is left unguessed.
    /// </summary>
    internal static int? ErrorLine(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var line = LinePattern().Match(message);
        if (line.Success && int.TryParse(line.Groups[1].Value, out var numbered) && numbered > 0)
        {
            return numbered;
        }

        var position = PositionPattern().Match(message);
        if (position.Success && int.TryParse(position.Groups[1].Value, out var row) && row > 0)
        {
            return row;
        }

        return message.Contains("header", StringComparison.OrdinalIgnoreCase) ? 1 : null;
    }

    [GeneratedRegex(@"\bline\s+(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"\b(\d+):\d+\b")]
    private static partial Regex PositionPattern();

    private readonly record struct CacheKey(string Source, string Palette, MermaidKinds AllowedTypes);
}
