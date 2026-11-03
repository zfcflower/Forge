using System.Text;
using System.Text.RegularExpressions;
using Forge.IO;

namespace Forge.Workspace;

/// <summary>
/// The spec tree of one workspace: Markdown files whose frontmatter carries <c>id</c> and <c>type</c>.
/// The index is scanned incrementally by file size and write time and swapped as an immutable snapshot, so
/// queries read a consistent view without blocking on a scan.
/// </summary>
public sealed class SpecGraph
{
    private const int MaxGrepLimit = 1000;
    private const int MaxGraphDepth = 10;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly SpecGraphOptions options;
    private readonly SemaphoreSlim mutex = new(1, 1);
    private readonly HashSet<string> stalePaths = new(PathComparer);
    private Snapshot snapshot = Snapshot.Empty;
    private bool loaded;

    public SpecGraph(string workspaceRoot, SpecGraphOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        WorkspaceRoot = WorkspacePaths.Normalize(workspaceRoot);
        this.options = options ?? new SpecGraphOptions();
    }

    public string WorkspaceRoot { get; }

    /// <summary>Re-scans the workspace, reading only files whose size or write time changed.</summary>
    public async Task<SpecRefreshResult> RefreshAsync(CancellationToken ct = default) => await RefreshAsync(force: false, ct).ConfigureAwait(false);

    /// <summary>
    /// Re-scans the workspace. <paramref name="force"/> re-reads every spec file instead of trusting size and
    /// write time, which catches an external write that kept both unchanged; equal content still counts as
    /// unchanged.
    /// </summary>
    public async Task<SpecRefreshResult> RefreshAsync(bool force, CancellationToken ct = default)
    {
        await mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ScanAsync(force, ct).ConfigureAwait(false);
        }
        finally
        {
            mutex.Release();
        }
    }

    public async Task<IReadOnlyList<SpecNodeSummary>> ListAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return snapshot.Ordered.Select(static entry => entry.Summary).ToArray();
    }

    /// <summary>
    /// Substring or regex search over spec file text, optionally narrowed by frontmatter metadata. Matches are
    /// paged in a stable order (path, then line); <see cref="SpecGrepPage.Total"/> counts every match.
    /// </summary>
    public async Task<SpecGrepPage> GrepAsync(SpecGrepQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Pattern);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        var limit = Math.Clamp(query.Limit, 1, MaxGrepLimit);
        var offset = Math.Max(0, query.Offset);
        var matches = new List<SpecMatch>();
        var total = 0;

        Regex? regex = null;
        if (query.Regex)
        {
            var regexOptions = RegexOptions.CultureInvariant | (query.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            regex = new Regex(query.Pattern, regexOptions, RegexTimeout);
        }

        var comparison = query.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var entry in snapshot.Ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (!MatchesMetadata(entry, query))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in TextLines.Split(entry.RawText))
            {
                lineNumber++;
                var isMatch = regex is null
                    ? line.Contains(query.Pattern, comparison)
                    : regex.IsMatch(line);
                if (!isMatch)
                {
                    continue;
                }

                if (total >= offset && matches.Count < limit)
                {
                    matches.Add(new SpecMatch(entry.Id, entry.RelativePath, lineNumber, line));
                }

                total++;
            }
        }

        var next = offset + matches.Count < total ? offset + matches.Count : (int?)null;
        return new SpecGrepPage(matches, total, next);
    }

    /// <summary>One spec with its declared links resolved and the links pointing back at it.</summary>
    public async Task<SpecNodeView?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return BuildView(snapshot, id);
    }

    /// <summary>Bounded slice of the spec tree: descendants, ancestors, or the links of one edge kind in both directions.</summary>
    public async Task<SpecGraphSlice?> GetGraphAsync(
        string id,
        SpecGraphDirection direction = SpecGraphDirection.Subtree,
        int depth = 1,
        SpecEdgeKind edge = SpecEdgeKind.DependsOn,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var root = FindById(snapshot, id);
        if (root is null)
        {
            return null;
        }

        var maxDepth = Math.Clamp(depth, 1, MaxGraphDepth);
        return direction switch
        {
            SpecGraphDirection.Subtree => BuildSubtree(snapshot, root, maxDepth),
            SpecGraphDirection.Ancestors => BuildAncestors(snapshot, root, maxDepth),
            _ => BuildNeighbors(snapshot, root, maxDepth, edge),
        };
    }

    public async Task<SpecValidationResult> ValidateAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var issues = new List<SpecIssue>();

        foreach (var pair in snapshot.ById.Where(static pair => pair.Value.Count > 1).OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var entry in pair.Value)
            {
                issues.Add(new SpecIssue(
                    SpecIssueCode.DuplicateId,
                    $"Id '{pair.Key}' is declared by {pair.Value.Count} files.",
                    pair.Key,
                    entry.RelativePath));
            }
        }

        foreach (var entry in snapshot.Ordered)
        {
            ct.ThrowIfCancellationRequested();
            var parent = entry.Frontmatter.Parent;
            if (!string.IsNullOrEmpty(parent) && !snapshot.ById.ContainsKey(parent))
            {
                issues.Add(new SpecIssue(SpecIssueCode.DanglingParent, $"Parent '{parent}' does not exist.", entry.Id, entry.RelativePath));
            }

            foreach (var target in ForwardTargets(entry, SpecEdgeKind.DependsOn)
                         .Concat(ForwardTargets(entry, SpecEdgeKind.References))
                         .Concat(ForwardTargets(entry, SpecEdgeKind.Implements))
                         .Concat(ForwardTargets(entry, SpecEdgeKind.Covers)))
            {
                if (!snapshot.ById.ContainsKey(target))
                {
                    issues.Add(new SpecIssue(SpecIssueCode.DanglingLink, $"Linked spec '{target}' does not exist.", entry.Id, entry.RelativePath));
                }
            }
        }

        issues.AddRange(FindCycles(snapshot));
        return new SpecValidationResult(issues);
    }

    public async Task<SpecNodeView> CreateAsync(SpecCreateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Type);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        var fullPath = ResolvePath(request.Path);
        WorkspacePaths.EnsurePlainPathInside(WorkspaceRoot, fullPath, nameof(request));

        await mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ScanAsync(force: false, ct).ConfigureAwait(false);

            if (snapshot.ById.ContainsKey(request.Id))
            {
                throw new InvalidOperationException($"A spec with id '{request.Id}' already exists.");
            }

            var fields = new List<SpecField>
            {
                new(SpecFrontmatter.IdField, [request.Id]),
                new(SpecFrontmatter.TypeField, [request.Type]),
                new(SpecFrontmatter.TitleField, [request.Title]),
            };
            if (!string.IsNullOrWhiteSpace(request.Status))
            {
                fields.Add(new(SpecFrontmatter.StatusField, [request.Status]));
            }

            if (!string.IsNullOrWhiteSpace(request.Parent))
            {
                fields.Add(new(SpecFrontmatter.ParentField, [request.Parent]));
            }

            AddList(fields, SpecFrontmatter.DependsOnField, request.DependsOn);
            AddList(fields, SpecFrontmatter.ReferencesField, request.References);
            AddList(fields, SpecFrontmatter.ImplementsField, request.Implements);
            AddList(fields, SpecFrontmatter.CoversField, request.Covers);
            AddList(fields, SpecFrontmatter.TagsField, request.Tags);

            var body = request.Body is null ? $"\n# {request.Title}\n" : request.Body.StartsWith('\n') ? request.Body : "\n" + request.Body;
            var frontmatter = new SpecFrontmatter(fields);
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await WriteNewFileAsync(fullPath, frontmatter.ToMarkdown(body), ct).ConfigureAwait(false);
            stalePaths.Add(fullPath);
            await ScanAsync(force: false, ct).ConfigureAwait(false);
            return BuildView(snapshot, request.Id)
                ?? throw new InvalidOperationException($"Spec '{request.Id}' was written to '{fullPath}' but not indexed.");
        }
        finally
        {
            mutex.Release();
        }
    }

    /// <summary>Edits frontmatter in place, keeping the body and every field the update does not name.</summary>
    public async Task<SpecNodeView?> UpdateFrontmatterAsync(string id, SpecFrontmatterUpdate update, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(update);

        await mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ScanAsync(force: false, ct).ConfigureAwait(false);

            var entry = FindById(snapshot, id);
            if (entry is null)
            {
                return null;
            }

            WorkspacePaths.EnsurePlainPathInside(WorkspaceRoot, entry.FullPath, nameof(id));

            var frontmatter = entry.Frontmatter;
            if (update.Set is not null)
            {
                foreach (var pair in update.Set)
                {
                    frontmatter = pair.Value is null ? frontmatter.Remove(pair.Key) : frontmatter.With(pair.Key, pair.Value);
                }
            }

            if (update.Add is not null)
            {
                foreach (var pair in update.Add)
                {
                    frontmatter = frontmatter.AddToList(pair.Key, pair.Value);
                }
            }

            if (update.Remove is not null)
            {
                foreach (var pair in update.Remove)
                {
                    frontmatter = frontmatter.RemoveFromList(pair.Key, pair.Value);
                }
            }

            if (string.IsNullOrWhiteSpace(frontmatter.Id) || string.IsNullOrWhiteSpace(frontmatter.Type))
            {
                throw new InvalidOperationException($"Update would remove 'id' or 'type' from spec '{id}'.");
            }

            var lineEnding = entry.RawText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            await AtomicFile.WriteAllBytesAsync(entry.FullPath, Utf8NoBom.GetBytes(frontmatter.ToMarkdown(entry.Body, lineEnding)), cancellationToken: ct).ConfigureAwait(false);
            stalePaths.Add(entry.FullPath);
            await ScanAsync(force: false, ct).ConfigureAwait(false);
            return BuildView(snapshot, frontmatter.Id!);
        }
        finally
        {
            mutex.Release();
        }
    }

    /// <summary>Deletes the spec file; links pointing at it become dangling and show up in <see cref="ValidateAsync"/>.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ScanAsync(force: false, ct).ConfigureAwait(false);

            var entry = FindById(snapshot, id);
            if (entry is null)
            {
                return false;
            }

            WorkspacePaths.EnsurePlainPathInside(WorkspaceRoot, entry.FullPath, nameof(id));
            File.Delete(entry.FullPath);
            stalePaths.Remove(entry.FullPath);
            await ScanAsync(force: false, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            mutex.Release();
        }
    }

    /// <summary>
    /// Writes a new file atomically: the create fails instead of overwriting, so a file that appeared after the
    /// caller's check keeps its original bytes.
    /// </summary>
    private async Task WriteNewFileAsync(string fullPath, string text, CancellationToken ct)
    {
        var bytes = Utf8NoBom.GetBytes(text);
        try
        {
            await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException exception) when (File.Exists(fullPath))
        {
            throw new InvalidOperationException($"Spec file '{fullPath}' already exists; it was left unchanged.", exception);
        }
    }

    private static void AddList(List<SpecField> fields, string name, IReadOnlyList<string>? values)
    {
        if (values is { Count: > 0 })
        {
            fields.Add(new SpecField(name, values.ToArray()));
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (loaded)
        {
            return;
        }

        await mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!loaded)
            {
                await ScanAsync(force: false, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            mutex.Release();
        }
    }

    private async Task<SpecRefreshResult> ScanAsync(bool force, CancellationToken ct)
    {
        var previous = snapshot.ByPath;
        var next = new Dictionary<string, SpecEntry>(previous.Count, PathComparer);
        var visited = new HashSet<string>(PathComparer);
        var added = 0;
        var updated = 0;
        var scanned = 0;

        foreach (var file in EnumerateSpecFiles(ct))
        {
            ct.ThrowIfCancellationRequested();
            scanned++;
            visited.Add(file.FullName);

            if (previous.TryGetValue(file.FullName, out var cached))
            {
                long length;
                long ticks;
                try
                {
                    length = file.Length;
                    ticks = file.LastWriteTimeUtc.Ticks;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                var markedStale = stalePaths.Remove(file.FullName);
                if (!force && !markedStale && cached.Length == length && cached.LastWriteTicks == ticks)
                {
                    next[file.FullName] = cached;
                    continue;
                }

                var reread = await ReadEntryAsync(file, ct).ConfigureAwait(false);
                if (reread is null)
                {
                    continue;
                }

                if (string.Equals(reread.RawText, cached.RawText, StringComparison.Ordinal)
                    && string.Equals(reread.RelativePath, cached.RelativePath, StringComparison.Ordinal))
                {
                    next[file.FullName] = cached with { Length = reread.Length, LastWriteTicks = reread.LastWriteTicks };
                    continue;
                }

                next[file.FullName] = reread;
                updated++;
                continue;
            }

            var entry = await ReadEntryAsync(file, ct).ConfigureAwait(false);
            if (entry is null)
            {
                continue;
            }

            next[file.FullName] = entry;
            added++;
        }

        var removed = previous.Keys.Count(path => !visited.Contains(path));
        snapshot = Snapshot.Create(next.Values);
        loaded = true;
        return new SpecRefreshResult(scanned, next.Count, added, updated, removed);
    }

    private IEnumerable<FileInfo> EnumerateSpecFiles(CancellationToken ct)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(WorkspaceRoot));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            FileSystemInfo[] children;
            try
            {
                children = directory.GetFileSystemInfos();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (child is DirectoryInfo subdirectory)
                {
                    if ((subdirectory.Attributes & FileAttributes.ReparsePoint) != 0
                        || options.SkippedDirectories.Contains(subdirectory.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    pending.Push(subdirectory);
                    continue;
                }

                if (child is not FileInfo file
                    || !string.Equals(file.Extension, options.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                long length;
                try
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    length = file.Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (length <= options.MaxFileBytes)
                {
                    yield return file;
                }
            }
        }
    }

    private async Task<SpecEntry?> ReadEntryAsync(FileInfo file, CancellationToken ct)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(file.FullName, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        if (!SpecFrontmatter.TryParse(text, out var frontmatter, out var body)
            || string.IsNullOrWhiteSpace(frontmatter.Id)
            || string.IsNullOrWhiteSpace(frontmatter.Type))
        {
            return null;
        }

        return new SpecEntry
        {
            FullPath = file.FullName,
            RelativePath = Path.GetRelativePath(WorkspaceRoot, file.FullName).Replace('\\', '/'),
            Length = file.Length,
            LastWriteTicks = file.LastWriteTimeUtc.Ticks,
            RawText = text,
            Body = body,
            Frontmatter = frontmatter,
        };
    }

    private SpecNodeView? BuildView(Snapshot current, string id)
    {
        var entry = FindById(current, id);
        if (entry is null)
        {
            return null;
        }

        var forward = new List<SpecLink>();
        foreach (var (kind, target) in DeclaredLinks(entry))
        {
            forward.Add(new SpecLink(kind, target, FindById(current, target)?.Summary));
        }

        var reverse = new List<SpecLink>();
        foreach (var other in current.Ordered)
        {
            if (string.Equals(other.Id, entry.Id, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (kind, target) in DeclaredLinks(other))
            {
                if (string.Equals(target, entry.Id, StringComparison.Ordinal))
                {
                    reverse.Add(new SpecLink(kind, other.Id, other.Summary));
                }
            }
        }

        return new SpecNodeView(entry.Summary, entry.Frontmatter, forward, reverse);
    }

    private static IEnumerable<(SpecEdgeKind Kind, string Target)> DeclaredLinks(SpecEntry entry)
    {
        foreach (var kind in new[] { SpecEdgeKind.Parent, SpecEdgeKind.DependsOn, SpecEdgeKind.References, SpecEdgeKind.Implements, SpecEdgeKind.Covers })
        {
            foreach (var target in ForwardTargets(entry, kind))
            {
                yield return (kind, target);
            }
        }
    }

    private static IEnumerable<string> ForwardTargets(SpecEntry entry, SpecEdgeKind kind)
    {
        switch (kind)
        {
            case SpecEdgeKind.Parent:
                if (!string.IsNullOrWhiteSpace(entry.Frontmatter.Parent))
                {
                    yield return entry.Frontmatter.Parent;
                }

                break;
            case SpecEdgeKind.DependsOn:
                foreach (var target in entry.Frontmatter.DependsOn)
                {
                    yield return target;
                }

                break;
            case SpecEdgeKind.References:
                foreach (var target in entry.Frontmatter.References)
                {
                    yield return target;
                }

                break;
            case SpecEdgeKind.Implements:
                foreach (var target in entry.Frontmatter.Implements)
                {
                    yield return target;
                }

                break;
            case SpecEdgeKind.Covers:
                foreach (var target in entry.Frontmatter.Covers)
                {
                    yield return target;
                }

                break;
        }
    }

    private static SpecGraphSlice BuildSubtree(Snapshot current, SpecEntry root, int depth)
    {
        var nodes = new List<SpecGraphNode> { new(root.Summary, 0) };
        var edges = new List<SpecGraphEdgeLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        var frontier = new List<SpecEntry> { root };
        for (var level = 1; level <= depth && frontier.Count > 0; level++)
        {
            var next = new List<SpecEntry>();
            foreach (var parent in frontier)
            {
                foreach (var child in current.Ordered)
                {
                    if (string.Equals(child.Frontmatter.Parent, parent.Id, StringComparison.Ordinal) && seen.Add(child.Id))
                    {
                        nodes.Add(new SpecGraphNode(child.Summary, level));
                        edges.Add(new SpecGraphEdgeLink(child.Id, SpecEdgeKind.Parent, parent.Id, true));
                        next.Add(child);
                    }
                }
            }

            frontier = next;
        }

        return new SpecGraphSlice(root.Id, SpecGraphDirection.Subtree, depth, nodes, edges);
    }

    private static SpecGraphSlice BuildAncestors(Snapshot current, SpecEntry root, int depth)
    {
        var nodes = new List<SpecGraphNode> { new(root.Summary, 0) };
        var edges = new List<SpecGraphEdgeLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        var currentEntry = root;
        for (var level = 1; level <= depth; level++)
        {
            var parentId = currentEntry.Frontmatter.Parent;
            if (string.IsNullOrWhiteSpace(parentId))
            {
                break;
            }

            var parent = FindById(current, parentId);
            edges.Add(new SpecGraphEdgeLink(currentEntry.Id, SpecEdgeKind.Parent, parentId, false));
            if (parent is null || !seen.Add(parent.Id))
            {
                break;
            }

            nodes.Add(new SpecGraphNode(parent.Summary, level));
            currentEntry = parent;
        }

        return new SpecGraphSlice(root.Id, SpecGraphDirection.Ancestors, depth, nodes, edges);
    }

    private static SpecGraphSlice BuildNeighbors(Snapshot current, SpecEntry root, int depth, SpecEdgeKind edge)
    {
        var nodes = new List<SpecGraphNode> { new(root.Summary, 0) };
        var edges = new List<SpecGraphEdgeLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { root.Id };
        var frontier = new List<SpecEntry> { root };

        for (var level = 1; level <= depth && frontier.Count > 0; level++)
        {
            var next = new List<SpecEntry>();
            foreach (var entry in frontier)
            {
                foreach (var target in ForwardTargets(entry, edge))
                {
                    edges.Add(new SpecGraphEdgeLink(entry.Id, edge, target, false));
                    var targetEntry = FindById(current, target);
                    if (targetEntry is not null && seen.Add(targetEntry.Id))
                    {
                        nodes.Add(new SpecGraphNode(targetEntry.Summary, level));
                        next.Add(targetEntry);
                    }
                }

                foreach (var other in current.Ordered)
                {
                    if (string.Equals(other.Id, entry.Id, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (ForwardTargets(other, edge).Contains(entry.Id, StringComparer.Ordinal))
                    {
                        edges.Add(new SpecGraphEdgeLink(other.Id, edge, entry.Id, true));
                        if (seen.Add(other.Id))
                        {
                            nodes.Add(new SpecGraphNode(other.Summary, level));
                            next.Add(other);
                        }
                    }
                }
            }

            frontier = next;
        }

        return new SpecGraphSlice(root.Id, SpecGraphDirection.Neighbors, depth, nodes, edges);
    }

    private static IEnumerable<SpecIssue> FindCycles(Snapshot current)
    {
        foreach (var entry in current.Ordered)
        {
            var visited = new List<string>();
            var currentId = entry.Id;
            string? cycleStart = null;
            while (true)
            {
                var node = FindById(current, currentId);
                var parent = node?.Frontmatter.Parent;
                if (string.IsNullOrWhiteSpace(parent))
                {
                    break;
                }

                if (string.Equals(parent, entry.Id, StringComparison.Ordinal))
                {
                    cycleStart = entry.Id;
                    break;
                }

                if (visited.Contains(parent, StringComparer.Ordinal))
                {
                    break;
                }

                visited.Add(parent);
                currentId = parent;
            }

            if (cycleStart is not null)
            {
                yield return new SpecIssue(
                    SpecIssueCode.ParentCycle,
                    $"Parent chain of '{entry.Id}' loops back to itself.",
                    entry.Id,
                    entry.RelativePath);
            }
        }
    }

    private static SpecEntry? FindById(Snapshot current, string id) =>
        current.ById.TryGetValue(id, out var entries) && entries.Count > 0 ? entries[0] : null;

    private static bool MatchesMetadata(SpecEntry entry, SpecGrepQuery query)
    {
        if (query.Type is not null && !string.Equals(entry.Frontmatter.Type, query.Type, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.Tag is not null && !entry.Frontmatter.Tags.Contains(query.Tag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.Parent is not null && !string.Equals(entry.Frontmatter.Parent, query.Parent, StringComparison.Ordinal))
        {
            return false;
        }

        if (query.DependsOn is not null && !entry.Frontmatter.DependsOn.Contains(query.DependsOn, StringComparer.Ordinal))
        {
            return false;
        }

        return true;
    }

    private string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(WorkspaceRoot, path));
        if (!WorkspacePaths.IsUnder(full, WorkspaceRoot))
        {
            throw new ArgumentException($"'{path}' is outside the workspace '{WorkspaceRoot}'.", nameof(path));
        }

        if (!string.Equals(Path.GetExtension(full), options.Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Spec files must end in '{options.Extension}'.", nameof(path));
        }

        return full;
    }

    private sealed record SpecEntry
    {
        public required string FullPath { get; init; }

        public required string RelativePath { get; init; }

        public required long Length { get; init; }

        public required long LastWriteTicks { get; init; }

        public required string RawText { get; init; }

        public required string Body { get; init; }

        public required SpecFrontmatter Frontmatter { get; init; }

        public string Id => Frontmatter.Id!;

        public SpecNodeSummary Summary => new(Id, Frontmatter.Type!, Frontmatter.Title, Frontmatter.Status, RelativePath);
    }

    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = Create(Array.Empty<SpecEntry>());

        private Snapshot(
            IReadOnlyDictionary<string, SpecEntry> byPath,
            IReadOnlyDictionary<string, IReadOnlyList<SpecEntry>> byId,
            IReadOnlyList<SpecEntry> ordered)
        {
            ByPath = byPath;
            ById = byId;
            Ordered = ordered;
        }

        public IReadOnlyDictionary<string, SpecEntry> ByPath { get; }

        public IReadOnlyDictionary<string, IReadOnlyList<SpecEntry>> ById { get; }

        public IReadOnlyList<SpecEntry> Ordered { get; }

        public static Snapshot Create(IEnumerable<SpecEntry> entries)
        {
            var byPath = new Dictionary<string, SpecEntry>(PathComparer);
            var byId = new Dictionary<string, List<SpecEntry>>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                byPath[entry.FullPath] = entry;
                if (!byId.TryGetValue(entry.Id, out var list))
                {
                    list = [];
                    byId[entry.Id] = list;
                }

                list.Add(entry);
            }

            var ordered = byPath.Values
                .OrderBy(static entry => entry.RelativePath, StringComparer.Ordinal)
                .ToArray();
            var idIndex = new Dictionary<string, IReadOnlyList<SpecEntry>>(byId.Count, StringComparer.Ordinal);
            foreach (var pair in byId)
            {
                pair.Value.Sort(static (left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
                idIndex[pair.Key] = pair.Value;
            }

            return new Snapshot(byPath, idIndex, ordered);
        }
    }
}
