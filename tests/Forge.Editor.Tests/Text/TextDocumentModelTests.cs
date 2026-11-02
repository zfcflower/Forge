using System.Text;
using Forge.Editor.Text;

namespace Forge.Editor.Tests.Text;

public class TextDocumentModelTests
{
    private static readonly string[] Fragments = ["a", "bc", "\r\n", "\n", "\r", "😀", "é", "xyz", " ", ""];

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(4242)]
    public void Random_single_edits_with_anchors_and_undo_redo_match_a_model(int seed)
    {
        var random = new Random(seed);
        var document = new TextDocument(string.Empty, maxUndoEntries: 100_000);
        var model = new StringBuilder();
        var anchors = new List<(Anchor Anchor, int Expected, AnchorBias Bias)>();
        var states = new List<string> { string.Empty };
        var changes = new List<TextChange>();
        var index = 0;

        for (var step = 0; step < 400; step++)
        {
            var roll = random.Next(10);
            if (roll < 7)
            {
                var offset = random.Next(model.Length + 1);
                var length = random.Next(Math.Min(model.Length - offset, 6) + 1);
                var text = string.Concat(Enumerable.Range(0, random.Next(0, 4)).Select(_ => Fragments[random.Next(Fragments.Length)]));
                if (random.Next(4) == 0 && anchors.Count < 12)
                {
                    var bias = random.Next(2) == 0 ? AnchorBias.Left : AnchorBias.Right;
                    anchors.Add((document.CreateAnchor(offset, bias), offset, bias));
                }

                MoveAll(anchors, offset, length, text);
                document.Replace(offset, length, text);
                model.Remove(offset, length).Insert(offset, text);
                states.RemoveRange(index + 1, states.Count - index - 1);
                changes.RemoveRange(index, changes.Count - index);
                states.Add(model.ToString());
                changes.Add(new TextChange(offset, length, text));
                index = states.Count - 1;
            }
            else if (roll < 9)
            {
                var undone = document.Undo();
                if (index > 0)
                {
                    var forward = changes[index - 1];
                    var removed = states[index - 1].Substring(forward.Offset, forward.Length);
                    MoveAll(anchors, forward.Offset, forward.Text.Length, removed);
                    index--;
                    Assert.NotNull(undone);
                    model.Clear().Append(states[index]);
                }
                else
                {
                    Assert.Null(undone);
                }
            }
            else
            {
                var redone = document.Redo();
                if (index < states.Count - 1)
                {
                    var forward = changes[index];
                    MoveAll(anchors, forward.Offset, forward.Length, forward.Text);
                    index++;
                    Assert.NotNull(redone);
                    model.Clear().Append(states[index]);
                }
                else
                {
                    Assert.Null(redone);
                }
            }

            Assert.Equal(model.ToString(), document.Snapshot.GetText());
            foreach (var (anchor, expected, _) in anchors)
            {
                Assert.Equal(expected, anchor.Offset);
            }
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(99)]
    public void Random_non_overlapping_batches_equal_their_reverse_application(int seed)
    {
        var random = new Random(seed);
        var document = new TextDocument(string.Concat(Enumerable.Range(0, 60).Select(_ => Fragments[random.Next(Fragments.Length)])));

        for (var round = 0; round < 150; round++)
        {
            var before = document.Snapshot.GetText();
            var changes = new List<TextChange>();
            var position = 0;
            while (position <= before.Length && changes.Count < 4)
            {
                var gap = random.Next(0, 6);
                var start = position + gap;
                if (start > before.Length)
                {
                    break;
                }

                var length = random.Next(0, Math.Min(4, before.Length - start) + 1);
                var text = Fragments[random.Next(Fragments.Length)];
                changes.Add(new TextChange(start, length, text));
                position = start + length + (length == 0 ? 1 : 0);
            }

            var expected = new StringBuilder(before);
            for (var i = changes.Count - 1; i >= 0; i--)
            {
                expected.Remove(changes[i].Offset, changes[i].Length).Insert(changes[i].Offset, changes[i].Text);
            }

            var version = document.Version;
            var snapshot = document.ApplyChanges(changes.ToArray());

            Assert.Equal(expected.ToString(), snapshot.GetText());
            Assert.Equal(version + (changes.Count > 0 ? 1 : 0), snapshot.Version);
        }
    }

    [Fact]
    public void Minimal_change_always_reproduces_the_new_text_from_the_old()
    {
        var random = new Random(8);
        var document = new TextDocument();

        for (var step = 0; step < 300; step++)
        {
            var old = document.Snapshot;
            var offset = random.Next(old.Length + 1);
            var length = random.Next(Math.Min(old.Length - offset, 5) + 1);
            var text = Fragments[random.Next(Fragments.Length)];
            TextChangedEventArgs? args = null;
            void Capture(object? _, TextChangedEventArgs e) => args = e;
            document.Changed += Capture;
            document.Replace(offset, length, text);
            document.Changed -= Capture;

            Assert.NotNull(args);
            var minimal = args.MinimalChange;
            var rebuilt = old.GetText().Remove(minimal.Offset, minimal.Length).Insert(minimal.Offset, minimal.Text);
            Assert.Equal(document.Snapshot.GetText(), rebuilt);
            Assert.True(minimal.Length <= old.Length);
        }
    }

    private static void MoveAll(List<(Anchor Anchor, int Expected, AnchorBias Bias)> anchors, int changeOffset, int changeLength, string text)
    {
        for (var i = 0; i < anchors.Count; i++)
        {
            var (anchor, expected, bias) = anchors[i];
            anchors[i] = (anchor, MoveAnchor(expected, bias, changeOffset, changeLength, text), bias);
        }
    }

    private static int MoveAnchor(int offset, AnchorBias bias, int changeOffset, int changeLength, string text)
    {
        var delta = text.Length - changeLength;
        if (offset < changeOffset)
        {
            return offset;
        }

        if (changeLength == 0)
        {
            if (offset == changeOffset)
            {
                return bias == AnchorBias.Right ? offset + text.Length : offset;
            }

            return offset + text.Length;
        }

        if (offset >= changeOffset + changeLength)
        {
            return offset + delta;
        }

        return changeOffset + (bias == AnchorBias.Right ? text.Length : 0);
    }
}
