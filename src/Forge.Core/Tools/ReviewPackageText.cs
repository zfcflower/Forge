using System.Text;

namespace Forge.Core.Tools;

/// <summary>What the reviewer is told before it reads anything: the intent, the checklist step when there is one, and the caller's focus.</summary>
public sealed record ReviewBrief(
    string Intent,
    string? StepTitle = null,
    string? StepSummary = null,
    string? StepVerification = null,
    string? Focus = null);

/// <summary>The rendered package, and whether the diff inside it is incomplete (cut here, or already truncated by the source).</summary>
public sealed record ReviewPackageBuild(string Text, bool DiffCut);

/// <summary>
/// Renders the task text of one review: why the change exists, the checklist step it belongs to, the change set git
/// answered with, the diff and what to concentrate on. Model-facing plain text in one pass, so a review costs one
/// string and no intermediate copies of the diff.
/// </summary>
public static class ReviewPackageText
{
    /// <summary>Cap on the diff inside the child's task message, not a user setting.</summary>
    public const int MaxDiffChars = 48_000;

    /// <summary>Cap on the commit and file lines together; the rest is left to <c>git_diff</c>.</summary>
    public const int MaxFileLines = 200;

    /// <summary>Cap per intent, summary, verification and focus field.</summary>
    public const int MaxFieldChars = 2_000;

    private const int ShortShaLength = 7;
    private const int LineEstimate = 72;

    private const string UntrackedNote =
        "The diff is git's output for tracked changes; a file listed as added with no diff section of its own is untracked - read it before you judge.";

    private const string ClosingLine = "Report findings as your instructions describe and end with the verdict line.";

    private const string RestOfDiff =
        "read the rest with git_diff, passing paths to narrow it or stat_only for the file list alone.";

    /// <summary>Renders the package text the reviewer receives as its task, and whether the diff inside it is incomplete.</summary>
    public static ReviewPackageBuild Render(ReviewBrief brief, ReviewChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(changeSet);

        var diff = changeSet.DiffText;
        var cut = diff.Length > MaxDiffChars;
        var diffCut = cut || changeSet.DiffTruncated;
        var listedLines = Math.Min(changeSet.Commits.Count, MaxFileLines) + Math.Min(changeSet.Files.Count, MaxFileLines);
        var builder = new StringBuilder(
            512
            + listedLines * LineEstimate
            + Math.Min(diff.Length, MaxDiffChars)
            + Math.Min(brief.Intent?.Length ?? 0, MaxFieldChars));

        builder.Append("This is a review request: read the change set and the diff below before you judge anything.\n\n");
        builder.Append("What this change is supposed to do:\n");
        if (string.IsNullOrWhiteSpace(brief.Intent))
        {
            builder.Append("(not stated)");
        }
        else
        {
            AppendClamped(builder, brief.Intent, MaxFieldChars);
        }

        builder.Append("\n\n");
        if (string.IsNullOrWhiteSpace(brief.StepTitle))
        {
            builder.Append("This review is not about one checklist step: it covers the branch and the uncommitted tree.\n\n");
        }
        else
        {
            builder.Append("Checklist step: ");
            AppendClamped(builder, brief.StepTitle, MaxFieldChars);
            builder.Append('\n');
            AppendField(builder, "What the step changed: ", brief.StepSummary);
            AppendField(builder, "How it was verified: ", brief.StepVerification);
        }

        builder.Append("The change set:\nworktree ").Append(changeSet.WorkingDirectory).Append('\n');
        builder.Append(string.IsNullOrWhiteSpace(changeSet.Branch) ? "HEAD is detached" : "branch " + changeSet.Branch).Append('\n');
        if (string.IsNullOrWhiteSpace(changeSet.Base))
        {
            builder.Append("head ").Append(ShortSha(changeSet.HeadCommit)).Append('\n');
        }
        else
        {
            builder.Append("base ").Append(ShortSha(changeSet.Base)).Append(" .. ").Append(ShortSha(changeSet.HeadCommit)).Append('\n');
        }

        builder.Append(Count(changeSet.Files.Count, "file")).Append(" changed, ").Append(Count(changeSet.Commits.Count, "commit")).Append('\n');

        var remaining = MaxFileLines;
        AppendCommits(builder, changeSet.Commits, ref remaining);
        AppendFiles(builder, changeSet.Files, ref remaining);

        builder.Append("\nDiff:\n");
        if (diff.Length == 0)
        {
            builder.Append("No tracked change is in the diff.\n");
        }
        else
        {
            var shown = cut ? DiffCutLength(diff) : diff.Length;
            builder.Append(diff, 0, shown);
            if (diff[shown - 1] != '\n')
            {
                builder.Append('\n');
            }

            if (cut)
            {
                builder.Append("[the diff was cut at ").Append(shown).Append(" of ").Append(diff.Length)
                    .Append(" characters, so it is incomplete: ").Append(RestOfDiff).Append("]\n");
            }
            else if (changeSet.DiffTruncated)
            {
                builder.Append("[the diff is incomplete: it was truncated before this package was built; ")
                    .Append(RestOfDiff).Append("]\n");
            }
        }

        builder.Append('\n').Append(UntrackedNote).Append('\n');

        if (!string.IsNullOrWhiteSpace(brief.Focus))
        {
            builder.Append("\nWhat to concentrate on:\n");
            AppendClamped(builder, brief.Focus, MaxFieldChars);
            builder.Append('\n');
        }

        builder.Append('\n').Append(ClosingLine);
        return new ReviewPackageBuild(builder.ToString(), diffCut);
    }

    /// <summary>"1 file" / "3 files": the model reads this line, so it must not come out as "1 files".</summary>
    private static string Count(int count, string noun) =>
        count == 1 ? "1 " + noun : count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + noun + "s";

    /// <summary>The first seven characters of a commit sha, or <c>unknown</c> when there is none.</summary>
    public static string ShortSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return "unknown";
        }

        var trimmed = sha.Trim();
        return trimmed.Length <= ShortShaLength ? trimmed : trimmed[..ShortShaLength];
    }

    /// <summary>One line per commit, newest first, sharing the line budget with the file list.</summary>
    private static void AppendCommits(StringBuilder builder, IReadOnlyList<ReviewCommit> commits, ref int remaining)
    {
        if (commits.Count == 0)
        {
            builder.Append("no commits in this change set\n");
            return;
        }

        var written = 0;
        for (; written < commits.Count && remaining > 0; written++)
        {
            var commit = commits[written];
            builder.Append(ShortSha(commit.Sha)).Append(' ');
            AppendClamped(builder, commit.Subject, MaxFieldChars);
            builder.Append('\n');
            remaining--;
        }

        if (written < commits.Count)
        {
            builder.Append("... and ").Append(commits.Count - written).Append(" more commits\n");
        }
    }

    /// <summary>One line per file, in git's order, sharing the line budget with the commit list.</summary>
    private static void AppendFiles(StringBuilder builder, IReadOnlyList<ReviewChangedFile> files, ref int remaining)
    {
        if (files.Count == 0)
        {
            builder.Append("Files: none\n");
            return;
        }

        builder.Append("Files:\n");
        var written = 0;
        for (; written < files.Count && remaining > 0; written++)
        {
            var file = files[written];
            builder.Append(file.Status).Append(' ').Append(file.Path);
            if (file.IsBinary)
            {
                builder.Append(" (binary)");
            }

            builder.Append('\n');
            remaining--;
        }

        if (written < files.Count)
        {
            builder.Append("... and ").Append(files.Count - written)
                .Append(" more files (run git_diff {\"stat_only\": true} to list them)\n");
        }
    }

    private static void AppendField(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append(label);
        AppendClamped(builder, value, MaxFieldChars);
        builder.Append('\n');
    }

    /// <summary>
    /// Appends <paramref name="text"/> cut at <paramref name="limit"/> characters, on a line boundary when the field has
    /// one inside the limit, and marks the cut with an ellipsis.
    /// </summary>
    private static void AppendClamped(StringBuilder builder, string text, int limit)
    {
        if (text.Length <= limit)
        {
            builder.Append(text);
            return;
        }

        var cut = text.LastIndexOf('\n', limit - 1, limit);
        if (cut <= 0)
        {
            // A word boundary is only worth taking when it keeps most of the field: a 2500-character URL behind "Fix "
            // would otherwise reach the reviewer as "Fix…". A hard cut inside an unbroken token reads fine.
            var space = text.LastIndexOf(' ', limit - 1, limit);
            cut = space >= limit * 3 / 4 ? space : limit;
        }

        if (cut <= 0)
        {
            cut = limit;
        }

        var slice = text.AsSpan(0, cut).TrimEnd();
        if (slice.Length > 0 && char.IsHighSurrogate(slice[^1]))
        {
            slice = slice[..^1];
        }

        builder.Append(slice).Append('…');
    }

    /// <summary>Where the diff stops: the last line boundary at or before the cap, or the cap itself when there is none.</summary>
    private static int DiffCutLength(string diff)
    {
        var cut = diff.LastIndexOf('\n', MaxDiffChars - 1, MaxDiffChars);
        return cut <= 0 ? MaxDiffChars : cut;
    }
}
