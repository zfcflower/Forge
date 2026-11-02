using Forge.Editor.Diff;
using Forge.Editor.Text;

namespace Forge.Editor.Tests.Diff;

public class DiffModelTests
{
    [Fact]
    public void Replacement_row_pairs_the_deleted_and_inserted_line()
    {
        var model = new DiffModel(TextSnapshot.FromText("a\nb\nc"), TextSnapshot.FromText("a\nx\nc"));

        Assert.Equal(3, model.RowCount);
        Assert.Equal(new DiffRow(DiffRowKind.Equal, 0, 0), model[0]);
        Assert.Equal(new DiffRow(DiffRowKind.Modified, 1, 1), model[1]);
        Assert.Equal(new DiffRow(DiffRowKind.Equal, 2, 2), model[2]);
        Assert.Equal(1, model.FirstChangedRow);
        Assert.Equal("b", model.GetLeftText(model[1]));
        Assert.Equal("x", model.GetRightText(model[1]));
        Assert.Equal("x", model.GetText(model[1]));
        Assert.Equal(new[] { (1, 1) }, model.Replacements(model.Hunks[0]));
        Assert.Equal(new DiffStats(2, 1, 1, 1), model.Stats);
        Assert.Equal(2, model.Stats.ChangedLines);
        Assert.False(model.IsEmpty);
    }

    [Fact]
    public void Removed_and_added_rows_have_no_counterpart_on_the_other_side()
    {
        var removed = new DiffModel(TextSnapshot.FromText("a\nb"), TextSnapshot.FromText("a"));
        var added = new DiffModel(TextSnapshot.FromText("a"), TextSnapshot.FromText("a\nb"));

        Assert.Equal(new DiffRow(DiffRowKind.Removed, 1, -1), removed.Rows[1]);
        Assert.Equal("b", removed.GetText(removed.Rows[1]));
        Assert.Equal(string.Empty, removed.GetRightText(removed.Rows[1]));
        Assert.Equal(new DiffRow(DiffRowKind.Added, -1, 1), added.Rows[1]);
        Assert.Equal("b", added.GetText(added.Rows[1]));
        Assert.Equal(string.Empty, added.GetLeftText(added.Rows[1]));
        Assert.True(added.Rows[1].IsChange);
        Assert.Equal(new DiffRow(DiffRowKind.Modified, 0, 0), added.Rows[0]);
    }

    [Fact]
    public void Several_deletes_then_one_insert_pair_once_and_leave_the_rest_removed()
    {
        var model = new DiffModel(TextSnapshot.FromText("a\nb\nc\nd"), TextSnapshot.FromText("a\nx"));

        Assert.Equal(
            new[] { DiffRowKind.Equal, DiffRowKind.Modified, DiffRowKind.Removed, DiffRowKind.Removed },
            model.Rows.Select(r => r.Kind).ToArray());
        Assert.Equal(new DiffRow(DiffRowKind.Modified, 1, 1), model.Rows[1]);
    }

    [Fact]
    public void Surplus_deletes_after_a_pair_are_removed_rows()
    {
        var model = new DiffModel(TextSnapshot.FromText("a\nb\nc"), TextSnapshot.FromText("a\nx"));

        Assert.Equal(
            new[] { DiffRowKind.Equal, DiffRowKind.Modified, DiffRowKind.Removed },
            model.Rows.Select(r => r.Kind).ToArray());
        Assert.Equal(new DiffRow(DiffRowKind.Removed, 2, -1), model.Rows[2]);
    }

    [Fact]
    public void Identical_documents_have_only_equal_rows_and_no_change_index()
    {
        var model = new DiffModel(TextSnapshot.FromText("same\ntext"), TextSnapshot.FromText("same\ntext"));

        Assert.True(model.IsEmpty);
        Assert.True(model.Stats.IsEmpty);
        Assert.Equal(-1, model.FirstChangedRow);
        Assert.Equal(2, model.RowCount);
        Assert.All(model.Rows, row => Assert.False(row.IsChange));
        Assert.Empty(model.Hunks);
        Assert.Equal(0, model.Stats.Hunks);
    }

    [Fact]
    public void Inline_script_and_hunks_are_the_diff_result_views()
    {
        var model = new DiffModel(TextSnapshot.FromText("a\nb"), TextSnapshot.FromText("a\nc\nb"));

        Assert.Same(model.Diff.Lines, model.Inline);
        Assert.Same(model.Diff.Hunks, model.Hunks);
        Assert.Equal(
            new[] { DiffLineKind.Equal, DiffLineKind.Insert, DiffLineKind.Equal },
            model.Inline.Select(l => l.Kind).ToArray());
        Assert.Equal(1, model.Stats.InsertedLines);
    }

    [Fact]
    public void Out_of_range_line_text_is_empty_rather_than_an_error()
    {
        var model = new DiffModel(TextSnapshot.FromText("one"), TextSnapshot.FromText("two"));

        Assert.Equal(string.Empty, model.GetOldLineText(5));
        Assert.Equal(string.Empty, model.GetNewLineText(-1));
        Assert.Equal("one", model.GetOldLineText(0));
        Assert.Equal("two", model.GetNewLineText(0));
    }

    [Fact]
    public void Options_are_passed_through_to_the_line_diff()
    {
        var model = new DiffModel(TextSnapshot.FromText("Hello"), TextSnapshot.FromText("hello"), new DiffOptions { IgnoreCase = true });

        Assert.True(model.IsEmpty);
        Assert.Equal(DiffRowKind.Equal, model[0].Kind);
    }

    [Fact]
    public void Null_snapshots_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new DiffModel(null!, TextSnapshot.FromText("x")));
        Assert.Throws<ArgumentNullException>(() => new DiffModel(TextSnapshot.FromText("x"), null!));
    }
}
