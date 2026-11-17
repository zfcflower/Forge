using Avalonia.Media.Imaging;
using Forge.App.ViewModels.Preview;
using Svg.Skia;

namespace Forge.App.Services;

/// <summary>
/// A picture the chat can show: a decoded bitmap, or a vector SVG picture. Exactly one of the two is set. Reference counted: the
/// creator holds the first reference, the picture cache one more while it keeps the entry, and every control showing it (a
/// thumbnail while attached, the viewer while open) takes one with <see cref="TryRetain"/> and gives it back with
/// <see cref="Release"/>. The bitmap or vector picture is disposed with the last reference, so a picture that is evicted from
/// the cache while still on screen stays valid until its last user lets go, and one nobody shows is freed promptly instead of
/// waiting for the garbage collector.
/// </summary>
internal sealed class ChatPicture
{
    private int references = 1;

    internal ChatPicture(Bitmap? bitmap, MermaidPicture? vector, string url, string? localPath)
    {
        Bitmap = bitmap;
        Vector = vector;
        Url = url;
        LocalPath = localPath;
    }

    public Bitmap? Bitmap { get; }

    public MermaidPicture? Vector { get; }

    public string Url { get; }

    public string? LocalPath { get; }

    /// <summary>The natural size in device-independent pixels.</summary>
    public Avalonia.Size Size => Bitmap is { } bitmap
        ? bitmap.Size
        : Vector is { } vector ? new Avalonia.Size(vector.Size.Width, vector.Size.Height) : default;

    /// <summary>The decoded pixels it keeps alive (0 for a vector picture), for the cache's memory budget.</summary>
    internal long PixelBytes => Bitmap is { } bitmap ? (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4 : 0;

    /// <summary>A picture for a diagram that something else owns (the Mermaid cache): it takes a reference of its own.</summary>
    internal static ChatPicture? ForDiagram(MermaidPicture diagram, string url) =>
        diagram.TryRetain() ? new ChatPicture(null, diagram, url, null) : null;

    /// <summary>
    /// The picture as a file the preview tab can open: a local file is itself; anything else (remote, a diagram, an inline SVG)
    /// is written under the temp folder - vector stays vector (an <c>.svg</c> copy of its text), a bitmap becomes a PNG - and
    /// that file is returned. Null when there is nothing to write or the write failed. Runs on a pool thread.
    /// </summary>
    internal string? Materialize()
    {
        if (LocalPath is { } local && File.Exists(local))
        {
            return local;
        }

        if (!TryRetain())
        {
            return null;
        }

        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "forge-pictures");
            Directory.CreateDirectory(folder);
            PictureSet.Sweep(folder);
            if (Vector is { Svg: { } svg })
            {
                return Store(folder, "diagram-", ".svg", svg, path => File.WriteAllText(path, svg));
            }

            if (Bitmap is { } bitmap)
            {
                return Store(folder, "picture-", ".png", Url + "|" + bitmap.PixelSize, path => bitmap.Save(path, new PngBitmapEncoderOptions()));
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            Release();
        }
    }

    /// <summary>The file for <paramref name="key"/> under <paramref name="folder"/>, written once: the name is the key's hash.</summary>
    private static string Store(string folder, string prefix, string extension, string key, Action<string> write)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        var path = Path.Combine(folder, prefix + hash.ToLowerInvariant() + extension);
        if (!File.Exists(path))
        {
            write(path);
        }

        return path;
    }

    /// <summary>Takes one more reference; false when the last one was already released (the picture is gone).</summary>
    internal bool TryRetain()
    {
        while (true)
        {
            var current = Volatile.Read(ref references);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref references, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    /// <summary>Gives one reference back; the last one disposes the bitmap / vector picture.</summary>
    internal void Release()
    {
        if (Interlocked.Decrement(ref references) == 0)
        {
            Bitmap?.Dispose();
            Vector?.Release();
        }
    }
}

/// <summary>
/// Loads the pictures a reply points at, for the thumbnails and the viewer: a local file (raster or SVG) or an http(s) address.
/// Reading and decoding run off the UI thread, bounded in size and edge; results are cached by location (stamped with the file's
/// write time for local ones) so a streaming reply that is re-rendered every flush never loads the same picture twice, and two
/// tiles that ask for the same one share a single load. A remote picture is only ever fetched with a plain GET of an http(s)
/// address, with a time and size bound; nothing else leaves the machine.
/// </summary>
internal static class ChatPictures
{
    internal const int MaxEdge = 2048;
    internal const long MaxBytes = 32L * 1024 * 1024;
    private const int CacheLimit = 48;
    private const long CacheByteLimit = 96L * 1024 * 1024;
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(20);

    private static readonly Dictionary<string, (long Stamp, Task<ChatPicture?> Load)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> Order = new();
    private static readonly Dictionary<string, long> Sizes = new(StringComparer.OrdinalIgnoreCase);
    private static long cachedBytes;
    private static HttpClient? http;

    /// <summary>The client for remote pictures; the composition root installs the one that goes through the app's network stack.</summary>
    internal static HttpClient Http
    {
        get => http ??= new HttpClient { Timeout = RemoteTimeout };
        set => http = value;
    }

    /// <summary>Whether <paramref name="path"/> names an SVG file.</summary>
    internal static bool IsSvgPath(string path) =>
        path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".svgz", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The picture at <paramref name="url"/> with a reference taken for the caller, who must <see cref="ChatPicture.Release"/>
    /// it when it stops showing it. Null when it cannot be loaded. UI thread only.
    /// </summary>
    internal static async Task<ChatPicture?> AcquireAsync(string url, string? workDir)
    {
        // A picture evicted between the load finishing and this continuation running is gone; the second pass loads it afresh.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var picture = await LoadAsync(url, workDir).ConfigureAwait(true);
            if (picture is null)
            {
                return null;
            }

            if (picture.TryRetain())
            {
                return picture;
            }
        }

        return null;
    }

    /// <summary>
    /// The cached load of <paramref name="url"/>: a web address, or a path resolved against <paramref name="workDir"/>. Null when it
    /// cannot be loaded (missing file, not an image, too big, network error). UI thread only (the cache is not synchronized).
    /// The result is owned by the cache; use <see cref="AcquireAsync"/> to show it.
    /// </summary>
    private static Task<ChatPicture?> LoadAsync(string url, string? workDir)
    {
        var target = LinkTarget.Classify(url, workDir);
        if (target.Kind != LinkTargetKind.Web && IsNetworkPath(target.Location))
        {
            // A UNC path would make Windows open an SMB connection (and offer the user's credentials) just by showing a reply.
            return Task.FromResult<ChatPicture?>(null);
        }

        long stamp = 0;
        string key;
        if (target.Kind == LinkTargetKind.Web)
        {
            key = target.Location;
        }
        else if (target.Kind is LinkTargetKind.SystemFile or LinkTargetKind.EditorFile)
        {
            key = target.Location;
            try
            {
                var info = new FileInfo(key);
                stamp = info.LastWriteTimeUtc.Ticks ^ info.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult<ChatPicture?>(null);
            }
        }
        else
        {
            return Task.FromResult<ChatPicture?>(null);
        }

        if (Cache.TryGetValue(key, out var cached) && cached.Stamp == stamp)
        {
            return cached.Load;
        }

        var load = target.Kind == LinkTargetKind.Web ? FetchAsync(key) : ReadAsync(key);
        if (Cache.ContainsKey(key))
        {
            Evict(key);
        }

        Order.Enqueue(key);
        Cache[key] = (stamp, load);
        Trim();
        _ = Settle(key, load);
        return load;
    }

    /// <summary>Drops the entry for <paramref name="key"/>; the cache's reference is released once its load has finished. Users keep theirs.</summary>
    private static void Evict(string key)
    {
        if (!Cache.Remove(key, out var entry))
        {
            return;
        }

        if (Sizes.Remove(key, out var bytes))
        {
            cachedBytes -= bytes;
        }

        _ = ReleaseWhenLoaded(entry.Load);
    }

    private static async Task ReleaseWhenLoaded(Task<ChatPicture?> load)
    {
        try
        {
            (await load.ConfigureAwait(true))?.Release();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }
    }

    /// <summary>Keeps the cache within its entry count and pixel budget, oldest first (never the newest entry).</summary>
    private static void Trim()
    {
        while (Order.Count > 1 && (Cache.Count > CacheLimit || cachedBytes > CacheByteLimit))
        {
            Evict(Order.Dequeue());
        }
    }

    /// <summary>
    /// A load that failed is not remembered (a timeout or a file still being written gets another try on the next render); one
    /// that succeeded is accounted against the pixel budget.
    /// </summary>
    private static async Task Settle(string key, Task<ChatPicture?> load)
    {
        ChatPicture? result = null;
        try
        {
            result = await load.ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
        }

        if (!Cache.TryGetValue(key, out var entry) || !ReferenceEquals(entry.Load, load))
        {
            return;
        }

        if (result is null)
        {
            Cache.Remove(key);
            return;
        }

        var bytes = result.PixelBytes;
        Sizes[key] = bytes;
        cachedBytes += bytes;
        Trim();
    }

    private static bool IsNetworkPath(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    private static Task<ChatPicture?> ReadAsync(string path) =>
        Task.Run<ChatPicture?>(() =>
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length is 0 or > MaxBytes)
                {
                    return null;
                }

                return Decode(File.ReadAllBytes(path), path, path, IsSvgPath(path));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return null;
            }
        });

    private static async Task<ChatPicture?> FetchAsync(string url)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(RemoteTimeout);
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }

            var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellation.Token).ConfigureAwait(false)) > 0)
            {
                memory.Write(buffer, 0, read);
                if (memory.Length > MaxBytes)
                {
                    return null;
                }
            }

            var bytes = memory.ToArray();
            var svg = type.Contains("svg", StringComparison.OrdinalIgnoreCase)
                || (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && LooksLikeSvg(bytes))
                || IsSvgPath(new Uri(url).AbsolutePath);
            return await Task.Run(() => Decode(bytes, url, null, svg)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static ChatPicture? Decode(byte[] bytes, string url, string? localPath, bool svg)
    {
        try
        {
            if (svg)
            {
                return LoadSvg(System.Text.Encoding.UTF8.GetString(bytes), url, localPath);
            }

            return new ChatPicture(ImageHeader.DecodeBounded(bytes, MaxEdge, BitmapInterpolationMode.MediumQuality), null, url, localPath);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static bool LooksLikeSvg(byte[] bytes)
    {
        var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        return head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Nesting deeper than this is refused: the SVG parser and the replay recurse once per level.</summary>
    private const int MaxSvgDepth = 64;

    /// <summary>
    /// Whether the SVG document is well-formed XML that refers to nothing outside itself. A text search for "href=" is not
    /// enough (an entity such as <c>&amp;#x68;ref</c> or odd quoting slips past it), so the document is read as XML with the
    /// DTD prohibited (no entity expansion, nothing resolved) and every decoded attribute value and style text is checked: an
    /// <c>href</c> must point inside the document (<c>#id</c>), and <c>url()</c> / <c>@import</c> must not leave it. Also caps
    /// the element depth so a hostile document cannot recurse the parser into a stack overflow.
    /// </summary>
    internal static bool IsSelfContained(string svg)
    {
        if (MermaidRenderService.HasExternalReference(svg))
        {
            return false;
        }

        try
        {
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxBytes,
            };
            using var reader = System.Xml.XmlReader.Create(new StringReader(svg.TrimStart((char)0xFEFF)), settings);
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case System.Xml.XmlNodeType.Element:
                        if (reader.Depth > MaxSvgDepth)
                        {
                            return false;
                        }

                        while (reader.MoveToNextAttribute())
                        {
                            var value = reader.Value.Trim();
                            if (reader.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase)
                                ? !value.StartsWith('#')
                                : MermaidRenderService.HasExternalReference(value))
                            {
                                return false;
                            }
                        }

                        reader.MoveToElement();
                        break;
                    case System.Xml.XmlNodeType.Text or System.Xml.XmlNodeType.CDATA:
                        if (MermaidRenderService.HasExternalReference(reader.Value))
                        {
                            return false;
                        }

                        break;
                }
            }

            return true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// An SVG document as a vector picture. Like a Mermaid diagram it is parsed by Svg.Skia and replayed, never rasterized; a
    /// document that refers to anything outside itself (an <c>href</c> to a file or address, a <c>url()</c> to one) is refused,
    /// so showing it cannot touch the disk or the network.
    /// </summary>
    internal static ChatPicture? LoadSvg(string svg, string url, string? localPath)
    {
        if (!IsSelfContained(svg))
        {
            return null;
        }

        SKSvg parsed;
        try
        {
            parsed = SKSvg.CreateFromSvg(svg);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }

        if (parsed.Picture is not { } picture || !(picture.CullRect.Width > 0) || !(picture.CullRect.Height > 0))
        {
            return null;
        }

        return new ChatPicture(null, new MermaidPicture(parsed, svg), url, localPath);
    }
}

/// <summary>
/// The pictures of the reply that was last opened large, in order, as files. The preview's previous/next buttons step through
/// it: an image whose file is in the set shows them, any other image (a file from the tree) does not.
/// </summary>
internal static class PictureSet
{
    private static string[] paths = [];
    private static bool swept;

    internal static void Set(IEnumerable<string> files) => paths = [.. files];

    internal static bool Contains(string path) => paths.Length > 1 && IndexOf(path) >= 0;

    /// <summary>The file <paramref name="step"/> places from <paramref name="path"/>, wrapping around; null when it is not in the set.</summary>
    internal static string? Step(string path, int step)
    {
        var at = IndexOf(path);
        return at < 0 ? null : paths[(at + step % paths.Length + paths.Length) % paths.Length];
    }

    private static int IndexOf(string path) =>
        Array.FindIndex(paths, candidate => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Once per run, removes the files of earlier runs that are over a week old from the temp picture folder.</summary>
    internal static void Sweep(string folder)
    {
        if (swept)
        {
            return;
        }

        swept = true;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))
                {
                    File.Delete(file);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
