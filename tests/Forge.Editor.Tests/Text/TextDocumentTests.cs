using System.Text;
using Forge.Editor.Text;

namespace Forge.Editor.Tests.Text;

public class TextDocumentTests
{
    [Fact]
    public void Batch_changes_apply_as_one_version_and_one_event()
    {
        var document = new TextDocument("abcd");
        var events = new List<TextChangedEventArgs>();
        document.Changed += (_, args) => events.Add(args);

        var snapshot = document.ApplyChanges(TextChange.Insert(0, "A"), TextChange.Delete(2, 1));

        Assert.Equal("Aabd", snapshot.GetText());
        Assert.Equal(1, snapshot.Version);
        Assert.Equal(1, document.Version);
        var single = Assert.Single(events);
        Assert.Equal(2, single.Changes.Count);
        Assert.Equal("abcd", single.Old.GetText());
        Assert.Same(snapshot, single.New);
    }

    [Fact]
    public void Batch_changes_refer_to_the_state_before_the_batch()
    {
        var document = new TextDocument("0123456789");

        document.ApplyChanges(new TextChange(1, 2, "AB"), new TextChange(6, 2, string.Empty));

        Assert.Equal("0AB34589", document.Snapshot.GetText());
    }

    [Fact]
    public void Empty_batch_returns_the_current_snapshot_without_a_version_bump()
    {
        var document = new TextDocument("x");
        var raised = 0;
        document.Changed += (_, _) => raised++;

        var snapshot = document.ApplyChanges(Array.Empty<TextChange>());

        Assert.Same(document.Snapshot, snapshot);
        Assert.Equal(0, snapshot.Version);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Overlapping_or_out_of_range_batches_are_rejected_and_leave_the_text_alone()
    {
        var document = new TextDocument("abcdef");

        Assert.Throws<ArgumentException>(() => document.ApplyChanges(new TextChange(1, 3, "x"), new TextChange(2, 1, "y")));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ApplyChanges(new TextChange(4, 5, "z")));
        Assert.Equal("abcdef", document.Snapshot.GetText());
        Assert.Equal(0, document.Version);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void Changed_event_reports_the_minimal_equivalent_change()
    {
        var document = new TextDocument("abcdef");
        TextChangedEventArgs? seen = null;
        document.Changed += (_, args) => seen = args;

        document.Replace(2, 2, "XYZ");

        Assert.NotNull(seen);
        Assert.Equal(new TextChange(2, 2, "XYZ"), seen.MinimalChange);
        Assert.Equal("abcdef", seen.Old.GetText());
        Assert.Equal("abXYZef", seen.New.GetText());
        Assert.Equal(0, seen.Old.Version);
        Assert.Equal(1, seen.New.Version);
    }

    [Fact]
    public void Minimal_change_of_a_batch_spans_from_first_difference_to_last()
    {
        var document = new TextDocument("0123456789");
        TextChangedEventArgs? seen = null;
        document.Changed += (_, args) => seen = args;

        document.ApplyChanges(new TextChange(1, 2, "AB"), new TextChange(6, 2, string.Empty));

        Assert.NotNull(seen);
        Assert.Equal(new TextChange(1, 7, "AB345"), seen.MinimalChange);
        Assert.Equal(2, seen.Changes.Count);
    }

    [Fact]
    public void Changed_event_line_helpers_describe_the_affected_lines()
    {
        var document = new TextDocument("ab\ncd");
        TextChangedEventArgs? seen = null;
        document.Changed += (_, args) => seen = args;

        document.Insert(1, "X\nY");

        Assert.NotNull(seen);
        Assert.Equal("aX\nYb\ncd", seen.New.GetText());
        Assert.Equal(0, seen.OldStartLine);
        Assert.Equal(0, seen.OldEndLine);
        Assert.Equal(0, seen.NewStartLine);
        Assert.Equal(1, seen.NewEndLine);
        Assert.Equal(new LinePosition(0, 1), seen.OldStartPosition);
    }

    [Fact]
    public void Snapshot_taken_before_an_edit_is_unchanged_by_it()
    {
        var document = new TextDocument("keep");
        var before = document.Snapshot;

        document.Insert(0, "new ");

        Assert.Equal("keep", before.GetText());
        Assert.Equal("new keep", document.Snapshot.GetText());
        Assert.Equal(0, before.Version);
    }

    [Fact]
    public void Undo_and_redo_restore_each_version()
    {
        var document = new TextDocument("base");
        document.Insert(4, "!");
        document.Replace(0, 1, "B");

        Assert.Equal("B" + "ase!", document.Snapshot.GetText());
        Assert.Equal("base!", document.Undo()!.GetText());
        Assert.Equal("base", document.Undo()!.GetText());
        Assert.Null(document.Undo());
        Assert.False(document.CanUndo);
        Assert.Equal("base!", document.Redo()!.GetText());
        Assert.Equal("Base!", document.Redo()!.GetText());
        Assert.Null(document.Redo());
    }

    [Fact]
    public void A_new_edit_clears_the_redo_stack()
    {
        var document = new TextDocument("a");
        document.Insert(1, "b");
        document.Undo();
        Assert.True(document.CanRedo);

        document.Insert(1, "c");

        Assert.False(document.CanRedo);
        Assert.Null(document.Redo());
    }

    [Fact]
    public void Nested_undo_groups_collapse_into_one_step()
    {
        var document = new TextDocument();
        using (document.BeginUndoGroup("outer"))
        {
            document.Insert(0, "a");
            using (document.BeginUndoGroup("inner"))
            {
                document.Insert(1, "b");
            }

            document.Insert(2, "c");
        }

        Assert.Equal("abc", document.Snapshot.GetText());
        Assert.True(document.CanUndo);
        Assert.Equal(string.Empty, document.Undo()!.GetText());
        Assert.False(document.CanUndo);
        Assert.Equal("abc", document.Redo()!.GetText());
    }

    [Fact]
    public void Inner_group_does_not_commit_before_the_outermost_dispose()
    {
        var document = new TextDocument();
        var outer = document.BeginUndoGroup("outer");
        var inner = document.BeginUndoGroup("inner");
        document.Insert(0, "x");
        inner.Dispose();

        Assert.False(document.CanUndo);
        outer.Dispose();
        Assert.True(document.CanUndo);
    }

    [Fact]
    public void Undo_is_refused_while_a_group_is_open()
    {
        var document = new TextDocument();
        document.Insert(0, "x");
        using var group = document.BeginUndoGroup();
        document.Insert(1, "y");

        Assert.Null(document.Undo());
        Assert.Equal("xy", document.Snapshot.GetText());
    }

    [Fact]
    public void Group_with_no_net_change_adds_no_undo_step()
    {
        var document = new TextDocument("q");
        using (document.BeginUndoGroup())
        {
            document.Insert(0, "x");
            document.Delete(0, 1);
        }

        Assert.False(document.CanUndo);
        Assert.Equal("q", document.Snapshot.GetText());
    }

    [Fact]
    public void Undo_history_is_bounded_by_max_entries()
    {
        var document = new TextDocument(maxUndoEntries: 2);
        document.Insert(0, "1");
        document.Insert(1, "2");
        document.Insert(2, "3");

        Assert.NotNull(document.Undo());
        Assert.NotNull(document.Undo());
        Assert.Null(document.Undo());
        Assert.Equal("1", document.Snapshot.GetText());
    }

    [Fact]
    public void History_changed_fires_on_edit_undo_redo_and_clear()
    {
        var document = new TextDocument();
        var raised = 0;
        document.HistoryChanged += (_, _) => raised++;

        document.Insert(0, "a");
        document.Undo();
        document.Redo();
        document.ClearHistory();

        Assert.Equal(4, raised);
        Assert.False(document.CanUndo);
        Assert.False(document.CanRedo);
    }

    [Fact]
    public void Undo_after_batch_reverts_every_change_of_the_batch()
    {
        var document = new TextDocument("0123456789");
        document.ApplyChanges(new TextChange(1, 2, "AB"), new TextChange(6, 2, string.Empty));

        Assert.Equal("0123456789", document.Undo()!.GetText());
        Assert.Equal("0AB34589", document.Redo()!.GetText());
    }

    [Fact]
    public void Insert_anchor_bias_decides_which_side_of_inserted_text_it_stays_on()
    {
        var document = new TextDocument("hello world");
        var left = document.CreateAnchor(6, AnchorBias.Left);
        var right = document.CreateAnchor(6, AnchorBias.Right);

        document.Insert(6, "X");

        Assert.Equal(6, left.Offset);
        Assert.Equal(7, right.Offset);
    }

    [Fact]
    public void Anchors_follow_edits_before_them_and_collapse_when_their_text_is_deleted()
    {
        var document = new TextDocument("abcdef");
        var anchor = document.CreateAnchor(3);

        document.Insert(0, "oh ");
        Assert.Equal(6, anchor.Offset);
        document.Delete(4, 4);
        Assert.Equal(4, anchor.Offset);
        document.Delete(1, 3);
        Assert.Equal(1, anchor.Offset);
    }

    [Fact]
    public void Replacement_over_an_anchor_places_it_by_bias()
    {
        var document = new TextDocument("abcdef");
        var right = document.CreateAnchor(3, AnchorBias.Right);
        var left = document.CreateAnchor(3, AnchorBias.Left);

        document.Replace(2, 2, "XYZ");

        Assert.Equal(5, right.Offset);
        Assert.Equal(2, left.Offset);
    }

    [Fact]
    public void Anchor_offsets_in_a_batch_follow_every_change()
    {
        var document = new TextDocument("0123456789");
        var anchor = document.CreateAnchor(8);

        document.ApplyChanges(new TextChange(1, 2, "AB"), new TextChange(6, 2, string.Empty));

        Assert.Equal(6, anchor.Offset);
        Assert.Equal('8', "0AB34589"[anchor.Offset]);
    }

    [Fact]
    public void Anchors_created_past_the_end_are_clamped()
    {
        var document = new TextDocument("ab");

        var anchor = document.CreateAnchor(50);
        var range = document.CreateRangeAnchor(-4, 99);

        Assert.Equal(2, anchor.Offset);
        Assert.Equal(0, range.Start);
        Assert.Equal(2, range.End);
        Assert.Equal(2, range.Length);
    }

    [Fact]
    public void Range_anchor_biases_decide_whether_boundary_insertions_are_included()
    {
        var document = new TextDocument("abcdef");
        var range = document.CreateRangeAnchor(2, 4);

        document.Insert(2, "X");
        Assert.Equal(3, range.Start);
        Assert.Equal(5, range.End);

        document.Insert(5, "Y");
        Assert.Equal(5, range.End);
    }

    [Fact]
    public void Reversed_range_anchor_is_normalised()
    {
        var document = new TextDocument("abcdef");

        var range = document.CreateRangeAnchor(4, 1);

        Assert.Equal(1, range.Start);
        Assert.Equal(4, range.End);
    }

    [Fact]
    public void Disposed_anchors_stop_moving()
    {
        var document = new TextDocument("abc");
        var anchor = document.CreateAnchor(1);
        var range = document.CreateRangeAnchor(0, 2);

        anchor.Dispose();
        range.Dispose();
        anchor.Dispose();
        document.Insert(0, "zz");

        Assert.True(anchor.IsDisposed);
        Assert.True(range.IsDisposed);
        Assert.Equal(1, anchor.Offset);
        Assert.Equal(0, range.Start);
        Assert.Equal(2, range.End);
    }

    [Fact]
    public void Two_inserts_at_one_position_leave_a_right_biased_anchor_after_both()
    {
        var document = new TextDocument("ab");
        var anchor = document.CreateAnchor(1, AnchorBias.Right);

        document.ApplyChanges(TextChange.Insert(1, "X"), TextChange.Insert(1, "Y"));

        Assert.Equal("aXYb", document.Snapshot.GetText());
        Assert.Equal(3, anchor.Offset);
    }

    [Fact]
    public void Dirty_flag_follows_saves_and_edits()
    {
        var document = new TextDocument("x");
        Assert.False(document.IsDirty);

        document.Insert(1, "y");
        Assert.True(document.IsDirty);

        document.MarkSaved();
        Assert.False(document.IsDirty);

        document.Undo();
        Assert.True(document.IsDirty);
    }

    [Fact]
    public void Position_encoding_and_language_are_kept_per_document()
    {
        var document = new TextDocument("a😀", positionEncoding: PositionEncoding.Utf8, path: "/tmp/x.cs")
        {
            LanguageId = "csharp",
        };

        Assert.Equal(PositionEncoding.Utf8, document.PositionEncoding);
        Assert.Equal("csharp", document.LanguageId);
        Assert.Equal("/tmp/x.cs", document.Path);
        Assert.Equal(new LinePosition(0, 5), document.Snapshot.OffsetToPosition(3));
    }

    [Fact]
    public async Task Concurrent_readers_see_whole_versions_while_a_writer_edits()
    {
        var document = new TextDocument(new string('a', 100));
        var errors = new List<string>();
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++)
            {
                var snapshot = document.Snapshot;
                var text = snapshot.GetText();
                if (text.Length != 100 + snapshot.Version || text.Any(c => c != 'a'))
                {
                    lock (errors)
                    {
                        errors.Add($"version {snapshot.Version} length {text.Length}");
                    }
                }
            }
        });

        for (var i = 0; i < 500; i++)
        {
            document.Insert(0, "a");
        }

        await reader.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(errors);
        Assert.Equal(600, document.Snapshot.Length);
    }

    [Fact]
    public async Task Concurrent_writers_serialise_without_losing_edits()
    {
        var document = new TextDocument();
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 250; i++)
            {
                document.Insert(0, "x");
            }
        })).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1000, document.Snapshot.Length);
        Assert.Equal(1000, document.Version);
    }

    [Fact]
    public void Invalid_constructor_arguments_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new TextDocument((TextSnapshot)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextDocument(maxUndoEntries: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChange(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChange(0, -1));
    }

    [Fact]
    public void Text_change_helpers_describe_the_edit()
    {
        var change = TextChange.Replace(2, 3, "xy");

        Assert.Equal(5, change.End);
        Assert.Equal(4, change.NewEnd);
        Assert.Equal(-1, change.Delta);
        Assert.False(change.IsInsert);
        Assert.False(change.IsDelete);
        Assert.True(TextChange.Insert(1, "a").IsInsert);
        Assert.True(TextChange.Delete(1, 2).IsDelete);
    }

    [Fact]
    public void Sequential_appends_produce_the_concatenated_text()
    {
        var document = new TextDocument();
        var builder = new StringBuilder();
        for (var i = 0; i < 40; i++)
        {
            document.Insert(document.Snapshot.Length, i.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(i.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal(builder.ToString(), document.Snapshot.GetText());
    }

}
