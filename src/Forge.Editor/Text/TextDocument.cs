namespace Forge.Editor.Text;

/// <summary>
/// Editable text buffer over an immutable rope. Writes are serialised by one lock; readers only ever see
/// <see cref="Snapshot"/> instances, which never mutate and can be used from any thread.
/// </summary>
public sealed class TextDocument
{
    private readonly object _gate = new();
    private readonly List<Anchor> _anchors = [];
    private readonly List<RangeAnchor> _rangeAnchors = [];
    private readonly Queue<TextChangedEventArgs?> _events = new();
    private readonly UndoHistory _history;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private TextSnapshot _snapshot;
    private GroupState? _group;
    private int _savedVersion;
    private bool _dispatching;

    public TextDocument(
        string? text = null,
        string? path = null,
        int version = 0,
        PositionEncoding positionEncoding = PositionEncoding.Utf16,
        int maxUndoEntries = 256)
    {
        _snapshot = new TextSnapshot(Rope.From(text), version, positionEncoding);
        _savedVersion = version;
        Path = path;
        _history = new UndoHistory(maxUndoEntries);
    }

    public TextDocument(TextSnapshot snapshot, string? path = null, int maxUndoEntries = 256)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _savedVersion = snapshot.Version;
        Path = path;
        _history = new UndoHistory(maxUndoEntries);
    }

    public string? Path { get; }

    /// <summary>Set by the language layer once detection has run.</summary>
    public string? LanguageId { get; set; }

    /// <summary>Encoding/BOM/line-ending information the document was loaded with; null for documents created in memory.</summary>
    public TextFileFormat? FileFormat { get; set; }

    public PositionEncoding PositionEncoding => Snapshot.PositionEncoding;

    public TextSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public int Version => Snapshot.Version;

    public bool IsDirty => Volatile.Read(ref _snapshot).Version != Volatile.Read(ref _savedVersion);

    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                return _history.CanUndo;
            }
        }
    }

    public bool CanRedo
    {
        get
        {
            lock (_gate)
            {
                return _history.CanRedo;
            }
        }
    }

    /// <summary>Raised on the editing thread, after the new snapshot is published and anchors have moved.</summary>
    public event EventHandler<TextChangedEventArgs>? Changed;

    public event EventHandler? HistoryChanged;

    public TextSnapshot ApplyChange(TextChange change) => ApplyChanges([change]);

    public TextSnapshot ApplyChanges(params TextChange[] changes) => ApplyChanges(changes.AsSpan());

    public TextSnapshot ApplyChanges(IReadOnlyList<TextChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return Snapshot;
        }

        return changes is TextChange[] array ? ApplyChanges(array.AsSpan()) : ApplyChanges(changes.ToArray().AsSpan());
    }

    /// <summary>Applies non-overlapping changes expressed against the current snapshot as one new version.</summary>
    public TextSnapshot ApplyChanges(ReadOnlySpan<TextChange> changes)
    {
        TextSnapshot result;
        lock (_gate)
        {
            result = ApplyLocked(changes, recordUndo: true);
        }

        DispatchEvents();
        return result;
    }

    public TextSnapshot Replace(int offset, int length, string text) => ApplyChanges(new TextChange(offset, length, text));

    public TextSnapshot Insert(int offset, string text) => ApplyChanges(new TextChange(offset, 0, text));

    public TextSnapshot Delete(int offset, int length) => ApplyChanges(new TextChange(offset, length));

    public TextSnapshot Remove(int offset, int length) => Delete(offset, length);

    public TextSnapshot? Undo() => Replay(undo: true);

    public TextSnapshot? Redo() => Replay(undo: false);

    public void ClearHistory()
    {
        lock (_gate)
        {
            _history.Clear();
            _events.Enqueue(null);
        }

        DispatchEvents();
    }

    /// <summary>Starts (or joins) an undo group so everything until the outermost dispose collapses into one step.</summary>
    public UndoGroup BeginUndoGroup(string? name = null)
    {
        lock (_gate)
        {
            _group ??= new GroupState(name, _snapshot);
            _group.Depth++;
            return new UndoGroup(this, _group);
        }
    }

    public Anchor CreateAnchor(int offset, AnchorBias bias = AnchorBias.Right)
    {
        lock (_gate)
        {
            var anchor = new Anchor(this, Math.Clamp(offset, 0, _snapshot.Length), bias);
            _anchors.Add(anchor);
            return anchor;
        }
    }

    public RangeAnchor CreateRangeAnchor(int start, int end, AnchorBias startBias = AnchorBias.Right, AnchorBias endBias = AnchorBias.Left)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        lock (_gate)
        {
            var anchor = new RangeAnchor(
                this,
                Math.Clamp(start, 0, _snapshot.Length),
                Math.Clamp(end, 0, _snapshot.Length),
                startBias,
                endBias);
            _rangeAnchors.Add(anchor);
            return anchor;
        }
    }

    public void MarkSaved() => Volatile.Write(ref _savedVersion, Version);

    public static async ValueTask<TextDocument> LoadAsync(string path, CancellationToken ct = default)
    {
        var file = await TextFileCodec.ReadAsync(path, ct).ConfigureAwait(false);
        if (file.IsBinary)
        {
            throw new NotSupportedException($"'{path}' does not look like a text file.");
        }

        return new TextDocument(file.Text, System.IO.Path.GetFullPath(path), version: 0)
        {
            FileFormat = file.Format,
        };
    }

    public async ValueTask SaveAsync(string? path = null, CancellationToken ct = default)
    {
        var target = path ?? Path ?? throw new InvalidOperationException("The document has no path to save to.");
        var format = FileFormat ?? TextFileFormat.Utf8;
        await _saveGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = Snapshot;
            await TextFileCodec.WriteAsync(target, snapshot, format, ct).ConfigureAwait(false);
            Volatile.Write(ref _savedVersion, snapshot.Version);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    internal void RemoveAnchor(Anchor anchor)
    {
        lock (_gate)
        {
            _anchors.Remove(anchor);
        }
    }

    internal void RemoveAnchor(RangeAnchor anchor)
    {
        lock (_gate)
        {
            _rangeAnchors.Remove(anchor);
        }
    }

    private TextSnapshot? Replay(bool undo)
    {
        TextSnapshot? result = null;
        lock (_gate)
        {
            if (_group is null)
            {
                var entry = undo ? _history.TakeUndo() : _history.TakeRedo();
                if (entry is not null)
                {
                    var change = undo ? entry.Inverse : entry.Forward;
                    ReplayCore(change);
                    _events.Enqueue(null);
                    result = _snapshot;
                }
            }
        }

        DispatchEvents();
        return result;
    }

    private void ReplayCore(TextChange change)
    {
        var old = _snapshot;
        var rope = old.Rope.Replace(change.Offset, change.Length, change.Text);
        Publish(old, rope, [change]);
    }

    private TextSnapshot ApplyLocked(ReadOnlySpan<TextChange> changes, bool recordUndo)
    {
        if (changes.IsEmpty)
        {
            return _snapshot;
        }

        var old = _snapshot;
        Validate(changes, old.Length);

        var rope = old.Rope;
        for (var i = changes.Length - 1; i >= 0; i--)
        {
            rope = rope.Replace(changes[i].Offset, changes[i].Length, changes[i].Text);
        }

        var applied = changes.ToArray();
        return Publish(old, rope, applied, recordUndo);
    }

    private TextSnapshot Publish(TextSnapshot old, Rope rope, TextChange[] applied, bool recordUndo = false)
    {
        var minimal = Collapse(old, rope, applied);
        var next = new TextSnapshot(rope, old.Version + 1, old.PositionEncoding);
        Volatile.Write(ref _snapshot, next);

        AdjustAnchors(applied);

        if (_group is not null)
        {
            _group.Track(minimal);
        }
        else if (recordUndo)
        {
            PushEntry(old, next, minimal, groupName: null);
        }

        _events.Enqueue(new TextChangedEventArgs(old, next, applied, minimal));
        return next;
    }

    private void PushEntry(TextSnapshot before, TextSnapshot after, TextChange forward, string? groupName)
    {
        var previousText = before.Rope.SliceToString(forward.Offset, forward.Length);
        var inverse = new TextChange(forward.Offset, forward.Text.Length, previousText);
        _history.Push(new UndoEntry(before, after, forward, inverse, groupName));
        _events.Enqueue(null);
    }

    internal void EndUndoGroup(GroupState state)
    {
        lock (_gate)
        {
            EndUndoGroupLocked(state);
        }

        DispatchEvents();
    }

    private void EndUndoGroupLocked(GroupState state)
    {
        if (--state.Depth > 0)
        {
            return;
        }

        if (ReferenceEquals(_group, state))
        {
            _group = null;
        }

        var after = _snapshot;
        if (!state.HasChanges || ReferenceEquals(state.Start.Rope, after.Rope))
        {
            return;
        }

        var bounds = state.StartBounds(state.Start.Length);
        var minimal = Merge(state.Start.Rope, state.Start.Length, after.Rope, bounds.MinOffset, bounds.MaxOldEnd);
        if (minimal.Length == 0 && minimal.Text.Length == 0)
        {
            return;
        }

        PushEntry(state.Start, after, minimal, state.Name);
    }

    private static void Validate(ReadOnlySpan<TextChange> changes, int length)
    {
        var previousEnd = 0;
        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            if (change.End > length)
            {
                throw new ArgumentOutOfRangeException(nameof(changes), change, "Change is outside the document.");
            }

            if (i > 0 && change.Offset < previousEnd)
            {
                throw new ArgumentException("Changes must be sorted and must not overlap.", nameof(changes));
            }

            previousEnd = change.End;
        }
    }

    private static int ChangesMinOffset(TextChange[] changes)
    {
        var min = int.MaxValue;
        foreach (var change in changes)
        {
            min = Math.Min(min, change.Offset);
        }

        return min == int.MaxValue ? 0 : min;
    }

    private static int ChangesMaxOldEnd(TextChange[] changes)
    {
        var max = 0;
        foreach (var change in changes)
        {
            max = Math.Max(max, change.End);
        }

        return max;
    }

    /// <summary>
    /// Collapses the applied changes into one equivalent replacement. A single edit is already minimal, so it is kept
    /// as-is; a batch is merged over the modified span only and then trimmed to the text that actually differs.
    /// </summary>
    private static TextChange Collapse(TextSnapshot old, Rope next, TextChange[] applied)
    {
        if (applied.Length == 1)
        {
            return applied[0];
        }

        return Merge(old.Rope, old.Length, next, ChangesMinOffset(applied), ChangesMaxOldEnd(applied));
    }

    private static TextChange Merge(Rope oldRope, int oldLength, Rope next, int oldStart, int oldEnd)
    {
        var newLength = next.Length;
        var tailLength = oldLength - oldEnd;
        var regionOld = oldEnd - oldStart;
        var regionNew = newLength - oldStart - tailLength;
        var prefixLimit = Math.Min(regionOld, regionNew);
        var prefix = prefixLimit == 0
            ? 0
            : oldRope.Slice(oldStart, prefixLimit).CommonPrefixLength(next.Slice(oldStart, prefixLimit), prefixLimit);
        var suffixLimit = Math.Min(regionOld - prefix, regionNew - prefix);
        var suffix = suffixLimit == 0
            ? 0
            : oldRope.Slice(oldEnd - suffixLimit, suffixLimit)
                .CommonSuffixLength(next.Slice(oldStart + regionNew - suffixLimit, suffixLimit), suffixLimit);
        var length = regionOld - prefix - suffix;
        var text = next.SliceToString(oldStart + prefix, regionNew - prefix - suffix);
        return new TextChange(oldStart + prefix, length, text);
    }

    private void DispatchEvents()
    {
        lock (_gate)
        {
            if (_dispatching || _events.Count == 0)
            {
                return;
            }

            _dispatching = true;
        }

        try
        {
            while (true)
            {
                TextChangedEventArgs? change;
                lock (_gate)
                {
                    if (_events.Count == 0)
                    {
                        _dispatching = false;
                        return;
                    }

                    change = _events.Dequeue();
                }

                if (change is null)
                {
                    HistoryChanged?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    Changed?.Invoke(this, change);
                }
            }
        }
        catch
        {
            lock (_gate)
            {
                _dispatching = false;
            }

            throw;
        }
    }

    private void AdjustAnchors(TextChange[] changes)
    {
        for (var i = 0; i < _anchors.Count; i++)
        {
            var anchor = _anchors[i];
            if (anchor.IsDisposed)
            {
                continue;
            }

            anchor.Offset = AdjustOffset(anchor.Offset, anchor.Bias, changes);
        }

        for (var i = 0; i < _rangeAnchors.Count; i++)
        {
            var anchor = _rangeAnchors[i];
            if (anchor.IsDisposed)
            {
                continue;
            }

            anchor.Start = AdjustOffset(anchor.Start, anchor.StartBias, changes);
            anchor.End = Math.Max(anchor.Start, AdjustOffset(anchor.End, anchor.EndBias, changes));
        }

        if (_anchors.Exists(static a => a.IsDisposed))
        {
            _anchors.RemoveAll(static a => a.IsDisposed);
        }

        if (_rangeAnchors.Exists(static a => a.IsDisposed))
        {
            _rangeAnchors.RemoveAll(static a => a.IsDisposed);
        }
    }

    private static int AdjustOffset(int offset, AnchorBias bias, TextChange[] changes)
    {
        var delta = 0;
        foreach (var change in changes)
        {
            if (change.Length == 0)
            {
                if (offset < change.Offset)
                {
                    return offset + delta;
                }

                if (offset == change.Offset)
                {
                    if (bias == AnchorBias.Left)
                    {
                        return offset + delta;
                    }

                    // A right-biased anchor moves past every insertion at its offset, so an insertion at the same
                    // offset further on (changes are ordered) must move it again rather than be skipped.
                    delta += change.Text.Length;
                    continue;
                }

                delta += change.Text.Length;
                continue;
            }

            if (offset < change.Offset)
            {
                return offset + delta;
            }

            if (offset >= change.End)
            {
                delta += change.Delta;
                continue;
            }

            return change.Offset + delta + (bias == AnchorBias.Right ? change.Text.Length : 0);
        }

        return offset + delta;
    }

    internal sealed class GroupState(string? name, TextSnapshot start)
    {
        public string? Name { get; } = name;

        public TextSnapshot Start { get; } = start;

        public int MinOffset { get; private set; } = int.MaxValue;

        public int MaxOldEnd { get; private set; }

        public int Depth { get; set; }

        public long TotalInserted { get; private set; }

        public long TotalDeleted { get; private set; }

        public bool HasChanges => MinOffset != int.MaxValue;

        public void Track(TextChange change)
        {
            MinOffset = Math.Min(MinOffset, change.Offset);
            MaxOldEnd = Math.Max(MaxOldEnd, change.End);
            TotalInserted += change.Text.Length;
            TotalDeleted += change.Length;
        }

        /// <summary>Changed window expressed in the group's starting coordinates, widened by the total edit volume.</summary>
        public (int MinOffset, int MaxOldEnd) StartBounds(int startLength)
        {
            if (MinOffset == int.MaxValue)
            {
                return (0, 0);
            }

            var min = Math.Max(0L, MinOffset - TotalInserted);
            var max = Math.Min(startLength, MaxOldEnd + TotalDeleted);
            return ((int)min, (int)max);
        }
    }
}

/// <summary>Disposing the outermost lease commits the group as one undo step.</summary>
public sealed class UndoGroup : IDisposable
{
    private readonly TextDocument _document;
    private readonly TextDocument.GroupState _state;
    private bool _disposed;

    internal UndoGroup(TextDocument document, TextDocument.GroupState state)
    {
        _document = document;
        _state = state;
    }

    public string? Name => _state.Name;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _document.EndUndoGroup(_state);
    }
}
