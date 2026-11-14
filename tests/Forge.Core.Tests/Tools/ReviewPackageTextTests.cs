using Forge.Core.Tools;
using Xunit;

namespace Forge.Core.Tests.Tools;

/// <summary>Layout of one review package: the diff cap, the line budget, the pluralisation and what the reviewer is told.</summary>
public sealed class ReviewPackageTextTests
{
    private const string UntrackedNote =
        "The diff is git's output for tracked changes; a file listed as added with no diff section of its own is untracked";

    private const string CutNote = "[the diff was cut at ";

    private const string TruncatedNote = "[the diff is incomplete: it was truncated before this package was built;";

    private static ReviewChangeSet ChangeSet(
        string diff = "",
        IReadOnlyList<ReviewChangedFile>? files = null,
        IReadOnlyList<ReviewCommit>? commits = null,
        bool diffTruncated = false) =>
        new()
        {
            WorkingDirectory = "work/tree",
            Branch = "feature",
            HeadCommit = "1234567890abcdef",
            Base = "0987654321fedcba",
            Files = files ?? [],
            Commits = commits ?? [],
            DiffText = diff,
            DiffTruncated = diffTruncated,
        };

    private static ReviewChangedFile File(int index) => new($"src/f{index:D3}.cs", "modified", false);

    private static ReviewCommit Commit(int index) =>
        new($"{index:D4}0123456789abcdef", $"subject {index:D3}");

    /// <summary>Lines of exactly 100 characters: 99 letters and a newline.</summary>
    private static string HundredCharLines(int lines) =>
        string.Concat(Enumerable.Repeat(new string('a', 99) + "\n", lines));

    /// <summary>One 70-character diff line, numbered so a cut can be located exactly.</summary>
    private static string DatedLine(int index) => $"line {index:D4} " + new string('a', 59) + "\n";

    private static string Render(ReviewBrief brief, ReviewChangeSet changeSet) => ReviewPackageText.Render(brief, changeSet).Text;

    private static ReviewBrief Brief(string intent = "add the request_review tool") => new(intent);

    [Fact]
    public void A_diff_of_exactly_the_cap_is_not_cut()
    {
        var diff = HundredCharLines(480);
        var changeSet = ChangeSet(diff, [File(0)]);

        var package = ReviewPackageText.Render(Brief(), changeSet);

        Assert.Equal(ReviewPackageText.MaxDiffChars, diff.Length);
        Assert.False(package.DiffCut);
        Assert.Contains(diff, package.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(CutNote, package.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("it was truncated", package.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_diff_one_character_over_the_cap_is_cut_on_a_line_boundary_and_says_how_to_read_the_rest()
    {
        var diff = string.Concat(Enumerable.Range(0, 700).Select(DatedLine)) + "UNIQUETAIL";
        var changeSet = ChangeSet(diff, [File(0)]);

        var package = ReviewPackageText.Render(Brief(), changeSet);

        Assert.Equal(49010, diff.Length);
        Assert.True(package.DiffCut);
        Assert.Contains("cut at 47949 of 49010 characters, so it is incomplete", package.Text, StringComparison.Ordinal);
        Assert.Contains("git_diff", package.Text, StringComparison.Ordinal);
        Assert.Contains("line 0684", package.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("line 0685", package.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("UNIQUETAIL", package.Text, StringComparison.Ordinal);

        var note = package.Text.IndexOf(CutNote, StringComparison.Ordinal);
        Assert.Equal('\n', package.Text[note - 1]);
    }

    [Fact]
    public void A_change_set_already_truncated_by_the_source_reports_the_diff_as_incomplete()
    {
        var changeSet = ChangeSet("diff --git a/src/f000.cs b/src/f000.cs\n+one\n", [File(0)], diffTruncated: true);

        var package = ReviewPackageText.Render(Brief(), changeSet);

        Assert.True(package.DiffCut);
        Assert.Contains(TruncatedNote, package.Text, StringComparison.Ordinal);
        Assert.Contains("git_diff", package.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(CutNote, package.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_count_line_pluralises_files_and_commits()
    {
        Assert.Contains("1 file changed, 0 commits", Render(Brief(), ChangeSet(files: [File(0)])), StringComparison.Ordinal);
        Assert.Contains("2 files changed, 0 commits", Render(Brief(), ChangeSet(files: [File(0), File(1)])), StringComparison.Ordinal);
        Assert.Contains("0 files changed, 1 commit", Render(Brief(), ChangeSet(commits: [Commit(0)])), StringComparison.Ordinal);
        Assert.Contains("0 files changed, 2 commits", Render(Brief(), ChangeSet(commits: [Commit(0), Commit(1)])), StringComparison.Ordinal);
    }

    [Fact]
    public void More_than_two_hundred_files_leave_a_trailer_and_the_first_two_hundred_lines()
    {
        var files = Enumerable.Range(0, 250).Select(File).ToArray();

        var text = Render(Brief(), ChangeSet(files: files));

        Assert.Contains("250 files changed, 0 commits", text, StringComparison.Ordinal);
        Assert.Contains("modified src/f000.cs", text, StringComparison.Ordinal);
        Assert.Contains("modified src/f199.cs", text, StringComparison.Ordinal);
        Assert.DoesNotContain("src/f200.cs", text, StringComparison.Ordinal);
        Assert.Contains("... and 50 more files (run git_diff {\"stat_only\": true} to list them)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Commits_and_files_share_the_same_two_hundred_line_budget()
    {
        var commits = Enumerable.Range(0, 150).Select(Commit).ToArray();
        var files = Enumerable.Range(0, 60).Select(File).ToArray();

        var text = Render(Brief(), ChangeSet(files: files, commits: commits));

        Assert.Contains("subject 149", text, StringComparison.Ordinal);
        Assert.DoesNotContain("more commits", text, StringComparison.Ordinal);
        Assert.Contains("modified src/f049.cs", text, StringComparison.Ordinal);
        Assert.DoesNotContain("src/f050.cs", text, StringComparison.Ordinal);
        Assert.Contains("... and 10 more files", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_diff_renders_the_no_tracked_change_line()
    {
        var text = Render(Brief(), ChangeSet(files: [File(0)]));

        Assert.Contains("Diff:\nNo tracked change is in the diff.\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_untracked_note_is_in_every_package()
    {
        Assert.Contains(UntrackedNote, Render(Brief(), ChangeSet()), StringComparison.Ordinal);
        Assert.Contains(UntrackedNote, Render(Brief(), ChangeSet("diff --git a/x b/x\n+one\n", [File(0)])), StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_brief_renders_the_step_title_summary_and_verification()
    {
        var brief = new ReviewBrief(
            "finish the review tool",
            "Wire request_review",
            "wired the tool and its source",
            "dotnet test --filter Review",
            "the verdict parsing and the diff cap");

        var text = Render(brief, ChangeSet(files: [File(0)]));

        Assert.Contains("Checklist step: Wire request_review\n", text, StringComparison.Ordinal);
        Assert.Contains("What the step changed: wired the tool and its source\n", text, StringComparison.Ordinal);
        Assert.Contains("How it was verified: dotnet test --filter Review\n", text, StringComparison.Ordinal);
        Assert.Contains("What to concentrate on:\nthe verdict parsing and the diff cap\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("not about one checklist step", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_brief_without_a_step_says_the_review_is_not_about_one_checklist_step()
    {
        var text = Render(Brief("review the branch"), ChangeSet(files: [File(0)]));

        Assert.Contains(
            "This review is not about one checklist step: it covers the branch and the uncommitted tree.",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Checklist step:", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_focus_is_left_out_of_the_package(string? focus)
    {
        var text = Render(new ReviewBrief("review the branch", Focus: focus), ChangeSet(files: [File(0)]));

        Assert.DoesNotContain("What to concentrate on:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_blank_focus_is_rendered_under_its_own_heading()
    {
        var text = Render(new ReviewBrief("review the branch", Focus: "cancellation paths"), ChangeSet(files: [File(0)]));

        Assert.Contains("What to concentrate on:\ncancellation paths\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_intent_longer_than_the_field_cap_is_clamped_and_marked_with_an_ellipsis()
    {
        var intent = new string('x', 2_500) + "INTENTTAIL";

        var text = Render(Brief(intent), ChangeSet(files: [File(0)]));

        Assert.Contains(new string('x', ReviewPackageText.MaxFieldChars) + "…", text, StringComparison.Ordinal);
        Assert.DoesNotContain("INTENTTAIL", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_intent_whose_only_space_is_its_first_word_is_cut_at_the_cap()
    {
        // A word boundary is only worth taking when it keeps most of the field: cutting at the space would hand the
        // reviewer "Fix…" instead of the 2000 characters the caller actually wrote.
        var intent = "Fix " + new string('x', 3_000) + "INTENTTAIL";

        var text = Render(Brief(intent), ChangeSet(files: [File(0)]));

        Assert.Contains("Fix " + new string('x', ReviewPackageText.MaxFieldChars - 4) + "…", text, StringComparison.Ordinal);
        Assert.DoesNotContain("INTENTTAIL", text, StringComparison.Ordinal);
    }
}
