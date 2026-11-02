using System.Text;

namespace Forge.Editor.Text;

/// <summary>
/// Immutable rope: a height-balanced tree of character leaves. Every edit returns a new rope that shares
/// untouched subtrees with the previous one, so a snapshot costs nothing to take and can be read from any thread.
/// </summary>
public abstract class Rope
{
    /// <summary>Leaves larger than this are split when text is inserted.</summary>
    public const int MaxLeafLength = 1024;

    private const int StackDepth = 64;

    public abstract int Length { get; }

    /// <summary>Number of <c>\n</c> characters in this rope; used to preallocate line indexes.</summary>
    public abstract int LineBreakCount { get; }

    internal abstract int Height { get; }

    public bool IsEmpty => Length == 0;

    public static Rope Empty { get; } = new LeafRope(ReadOnlyMemory<char>.Empty);

    public static Rope From(string? text) => string.IsNullOrEmpty(text) ? Empty : BuildBalanced(text.AsMemory());

    public static Rope From(ReadOnlySpan<char> text) => text.IsEmpty ? Empty : BuildBalanced(text.ToString().AsMemory());

    public char this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return CharAt(this, index);
        }
    }

    public Rope Insert(int offset, ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return this;
        }

        return Replace(offset, 0, text);
    }

    public Rope Remove(int offset, int length) => Replace(offset, length, ReadOnlySpan<char>.Empty);

    public Rope Replace(int offset, int length, ReadOnlySpan<char> text)
    {
        ValidateRange(offset, length);
        if (length == 0 && text.IsEmpty)
        {
            return this;
        }

        var head = Slice(0, offset);
        var tail = Slice(offset + length, Length - offset - length);
        var middle = text.IsEmpty ? Empty : From(text);
        return Join(Join(head, middle), tail);
    }

    public Rope Slice(int offset, int length)
    {
        ValidateRange(offset, length);
        if (length == 0)
        {
            return Empty;
        }

        return offset == 0 && length == Length ? this : SliceCore(this, offset, length);
    }

    public void CopyTo(int offset, Span<char> destination)
    {
        ValidateRange(offset, destination.Length);
        if (destination.IsEmpty)
        {
            return;
        }

        CopyCore(this, offset, destination);
    }

    public string SliceToString(int offset, int length)
    {
        ValidateRange(offset, length);
        if (length == 0)
        {
            return string.Empty;
        }

        if (this is LeafRope leaf && offset == 0 && length == Length)
        {
            return leaf.Text.ToString();
        }

        return string.Create(length, (Rope: this, Offset: offset), static (span, state) => state.Rope.CopyTo(state.Offset, span));
    }

    public string GetText() => SliceToString(0, Length);

    public override string ToString() => GetText();

    public RopeChunks Chunks => new(this);

    public RopeChunks.Enumerator GetEnumerator() => new(this);

    /// <summary>Length of the common prefix with <paramref name="other"/>, compared at most up to <paramref name="limit"/> characters.</summary>
    public int CommonPrefixLength(Rope other, int limit)
    {
        ArgumentNullException.ThrowIfNull(other);
        var max = Math.Min(Math.Min(Length, other.Length), Math.Max(limit, 0));
        if (max == 0)
        {
            return 0;
        }

        var scanned = 0;
        using var left = new RopeChunks.Enumerator(this);
        using var right = new RopeChunks.Enumerator(other);
        var leftChunk = ReadOnlySpan<char>.Empty;
        var rightChunk = ReadOnlySpan<char>.Empty;
        var leftOffset = 0;
        var rightOffset = 0;
        while (scanned < max)
        {
            if (leftOffset == leftChunk.Length)
            {
                if (!left.MoveNext())
                {
                    break;
                }

                leftChunk = left.Current.Span;
                leftOffset = 0;
            }

            if (rightOffset == rightChunk.Length)
            {
                if (!right.MoveNext())
                {
                    break;
                }

                rightChunk = right.Current.Span;
                rightOffset = 0;
            }

            var span = Math.Min(Math.Min(leftChunk.Length - leftOffset, rightChunk.Length - rightOffset), max - scanned);
            var matched = leftChunk.Slice(leftOffset, span).CommonPrefixLength(rightChunk.Slice(rightOffset, span));
            scanned += matched;
            if (matched != span)
            {
                break;
            }

            leftOffset += span;
            rightOffset += span;
        }

        return scanned;
    }

    /// <summary>Length of the common suffix with <paramref name="other"/>, compared at most up to <paramref name="limit"/> characters.</summary>
    public int CommonSuffixLength(Rope other, int limit)
    {
        ArgumentNullException.ThrowIfNull(other);
        var max = Math.Min(Math.Min(Length, other.Length), Math.Max(limit, 0));
        if (max == 0)
        {
            return 0;
        }

        var scanned = 0;
        using var left = new ReverseLeafEnumerator(this);
        using var right = new ReverseLeafEnumerator(other);
        var leftChunk = ReadOnlySpan<char>.Empty;
        var rightChunk = ReadOnlySpan<char>.Empty;
        var leftOffset = 0;
        var rightOffset = 0;
        while (scanned < max)
        {
            if (leftOffset == 0)
            {
                if (!left.MoveNext())
                {
                    break;
                }

                leftChunk = left.Current.Span;
                leftOffset = leftChunk.Length;
            }

            if (rightOffset == 0)
            {
                if (!right.MoveNext())
                {
                    break;
                }

                rightChunk = right.Current.Span;
                rightOffset = rightChunk.Length;
            }

            var span = Math.Min(Math.Min(leftOffset, rightOffset), max - scanned);
            var matched = CountMatchingSuffix(leftChunk.Slice(leftOffset - span, span), rightChunk.Slice(rightOffset - span, span));
            scanned += matched;
            if (matched != span)
            {
                break;
            }

            leftOffset -= span;
            rightOffset -= span;
        }

        return scanned;
    }

    /// <summary>FNV-1a (64 bit) over the UTF-16 code units of the whole rope.</summary>
    public ulong ContentHash()
    {
        ulong hash = 14695981039346656037UL;
        foreach (var chunk in this)
        {
            hash = Fnv(chunk.Span, hash);
        }

        return hash;
    }

    internal static ulong Fnv(ReadOnlySpan<char> text, ulong hash = 14695981039346656037UL)
    {
        foreach (var c in text)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }

        return hash;
    }

    internal static Rope Join(Rope left, Rope right)
    {
        if (left.Length == 0)
        {
            return right;
        }

        if (right.Length == 0)
        {
            return left;
        }

        if (left is LeafRope a && right is LeafRope b && a.Length + b.Length <= MaxLeafLength)
        {
            return new LeafRope(string.Concat(a.Text.Span, b.Text.Span).AsMemory());
        }

        if (left.Height > right.Height + 1)
        {
            var node = (BranchRope)left;
            return Balance(new BranchRope(node.Left, Join(node.Right, right)));
        }

        if (right.Height > left.Height + 1)
        {
            var node = (BranchRope)right;
            return Balance(new BranchRope(Join(left, node.Left), node.Right));
        }

        return new BranchRope(left, right);
    }

    private static Rope Balance(BranchRope node)
    {
        var leftHeight = node.Left.Height;
        var rightHeight = node.Right.Height;
        if (leftHeight > rightHeight + 1)
        {
            var left = (BranchRope)node.Left;
            if (left.Left.Height >= left.Right.Height)
            {
                return new BranchRope(left.Left, new BranchRope(left.Right, node.Right));
            }

            var pivot = (BranchRope)left.Right;
            return new BranchRope(new BranchRope(left.Left, pivot.Left), new BranchRope(pivot.Right, node.Right));
        }

        if (rightHeight > leftHeight + 1)
        {
            var right = (BranchRope)node.Right;
            if (right.Right.Height >= right.Left.Height)
            {
                return new BranchRope(new BranchRope(node.Left, right.Left), right.Right);
            }

            var pivot = (BranchRope)right.Left;
            return new BranchRope(new BranchRope(node.Left, pivot.Left), new BranchRope(pivot.Right, right.Right));
        }

        return node;
    }

    private static Rope SliceCore(Rope rope, int offset, int length)
    {
        if (offset == 0 && length == rope.Length)
        {
            return rope;
        }

        switch (rope)
        {
            case LeafRope leaf:
                return new LeafRope(leaf.Text.Slice(offset, length));
            case BranchRope branch:
            {
                var leftLength = branch.Left.Length;
                if (offset + length <= leftLength)
                {
                    return SliceCore(branch.Left, offset, length);
                }

                if (offset >= leftLength)
                {
                    return SliceCore(branch.Right, offset - leftLength, length);
                }

                var fromLeft = SliceCore(branch.Left, offset, leftLength - offset);
                var fromRight = SliceCore(branch.Right, 0, length - (leftLength - offset));
                return Join(fromLeft, fromRight);
            }

            default:
                throw new InvalidOperationException("Unknown rope node.");
        }
    }

    private static void CopyCore(Rope rope, int offset, Span<char> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        switch (rope)
        {
            case LeafRope leaf:
                leaf.Text.Span.Slice(offset, destination.Length).CopyTo(destination);
                return;
            case BranchRope branch:
            {
                var leftLength = branch.Left.Length;
                if (offset + destination.Length <= leftLength)
                {
                    CopyCore(branch.Left, offset, destination);
                    return;
                }

                if (offset >= leftLength)
                {
                    CopyCore(branch.Right, offset - leftLength, destination);
                    return;
                }

                var fromLeft = leftLength - offset;
                CopyCore(branch.Left, offset, destination[..fromLeft]);
                CopyCore(branch.Right, 0, destination[fromLeft..]);
                return;
            }

            default:
                throw new InvalidOperationException("Unknown rope node.");
        }
    }

    private static char CharAt(Rope rope, int index)
    {
        while (true)
        {
            switch (rope)
            {
                case LeafRope leaf:
                    return leaf.Text.Span[index];
                case BranchRope branch:
                    if (index < branch.Left.Length)
                    {
                        rope = branch.Left;
                        continue;
                    }

                    index -= branch.Left.Length;
                    rope = branch.Right;
                    continue;
                default:
                    throw new InvalidOperationException("Unknown rope node.");
            }
        }
    }

    private static Rope BuildBalanced(ReadOnlyMemory<char> text)
    {
        if (text.Length <= MaxLeafLength)
        {
            return new LeafRope(text);
        }

        var split = text.Length / 2 / MaxLeafLength * MaxLeafLength;
        if (split <= 0)
        {
            split = text.Length / 2;
        }

        return new BranchRope(BuildBalanced(text[..split]), BuildBalanced(text[split..]));
    }

    private static int CountMatchingSuffix(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var count = 0;
        while (count < left.Length && left[left.Length - 1 - count] == right[right.Length - 1 - count])
        {
            count++;
        }

        return count;
    }

    private struct ReverseLeafEnumerator : IDisposable
    {
        private readonly Rope[] _stack;
        private int _depth;
        private ReadOnlyMemory<char> _current;

        public ReverseLeafEnumerator(Rope root)
        {
            _stack = new Rope[Math.Max(StackDepth, root.Height + 2)];
            _depth = 0;
            _current = default;
            Push(root);
        }

        public readonly ReadOnlyMemory<char> Current => _current;

        public bool MoveNext()
        {
            while (_depth > 0)
            {
                var node = _stack[--_depth];
                if (node is LeafRope leaf)
                {
                    _current = leaf.Text;
                    return true;
                }

                var branch = (BranchRope)node;
                Push(branch.Left);
                Push(branch.Right);
            }

            _current = default;
            return false;
        }

        public readonly void Dispose()
        {
        }

        private void Push(Rope node)
        {
            if (node.Length == 0)
            {
                return;
            }

            _stack[_depth++] = node;
        }
    }

    private void ValidateRange(int offset, int length)
    {
        if ((uint)offset > (uint)Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Offset is outside the rope.");
        }

        if ((uint)length > (uint)(Length - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Length exceeds the rope.");
        }
    }

    /// <summary>Allocator-free in-order iteration over the leaf memories of a rope.</summary>
    public readonly struct RopeChunks(Rope rope) : IEnumerable<ReadOnlyMemory<char>>
    {
        public Enumerator GetEnumerator() => new(rope);

        IEnumerator<ReadOnlyMemory<char>> IEnumerable<ReadOnlyMemory<char>>.GetEnumerator() => GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<ReadOnlyMemory<char>>
        {
            private readonly Rope[] _stack;
            private readonly Rope _root;
            private int _depth;
            private ReadOnlyMemory<char> _current;

            internal Enumerator(Rope root)
            {
                _root = root;
                _stack = new Rope[Math.Max(StackDepth, root.Height + 2)];
                _depth = 0;
                _current = default;
                Push(root);
            }

            public readonly ReadOnlyMemory<char> Current => _current;

            readonly object System.Collections.IEnumerator.Current => _current;

            public bool MoveNext()
            {
                while (_depth > 0)
                {
                    var node = _stack[--_depth];
                    if (node is LeafRope leaf)
                    {
                        _current = leaf.Text;
                        return true;
                    }

                    var branch = (BranchRope)node;
                    Push(branch.Right);
                    Push(branch.Left);
                }

                _current = default;
                return false;
            }

            public void Reset() => throw new NotSupportedException();

            public readonly void Dispose()
            {
            }

            private void Push(Rope node)
            {
                if (node.Length == 0)
                {
                    return;
                }

                _stack[_depth++] = node;
            }
        }
    }
}
