using System.Text;
using System.Text.Json;

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
    private const int ShortShaLength = 7;
    private const int LineEstimate = 72;

    private const string UntrackedNote =
        "The diff is git's output for tracked changes; a file listed as added with no diff section of its own is untracked - read it before you judge.";

    private const string ClosingLine = "Report findings as your instructions describe and end with the verdict line.";

    /// <summary>Renders the package text the reviewer receives as its task within <paramref name="limits"/> (the package defaults when null), and whether the diff inside it is incomplete.</summary>
    public static ReviewPackageBuild Render(ReviewBrief brief, ReviewChangeSet changeSet, ReviewPackageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(changeSet);
        var cap = (limits ?? ReviewPackageLimits.Default).Sanitized();

        var diff = changeSet.DiffText;
        var cut = diff.Length > cap.DiffChars;
        var diffCut = cut || changeSet.DiffTruncated;
        var listedLines = Math.Min(changeSet.Commits.Count, cap.CommitLines) + Math.Min(changeSet.Files.Count, cap.FileLines);
        var builder = new StringBuilder(
            512
            + listedLines * LineEstimate
            + Math.Min(diff.Length, cap.DiffChars)
            + Math.Min(brief.Intent?.Length ?? 0, cap.FieldChars));

        builder.Append("This is a review request: read the change set and the diff below before you judge anything.\n\n");
        builder.Append("What this change is supposed to do:\n");
        if (string.IsNullOrWhiteSpace(brief.Intent))
        {
            builder.Append("(not stated)");
        }
        else
        {
            AppendClamped(builder, brief.Intent, cap.FieldChars);
        }

        builder.Append("\n\n");
        if (string.IsNullOrWhiteSpace(brief.StepTitle))
        {
            builder.Append("This review is not about one checklist step: it covers the branch and the uncommitted tree.\n\n");
        }
        else
        {
            builder.Append("Checklist step: ");
            AppendClamped(builder, brief.StepTitle, cap.FieldChars);
            builder.Append('\n');
            AppendField(builder, "What the step changed: ", brief.StepSummary, cap.FieldChars);
            AppendField(builder, "How it was verified: ", brief.StepVerification, cap.FieldChars);
            builder.Append('\n');
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

        builder.Append(Count(changeSet.Files.Count, "file")).Append(" changed, ")
            .Append(changeSet.CommitsTruncated ? "at least " : string.Empty)
            .Append(Count(changeSet.Commits.Count, "commit")).Append('\n');

        if (changeSet.Paths.Count > 0)
        {
            // The caller chooses the list, so it is held to the same field cap as the other caller-written text: thousands
            // of paths must not push the diff out of the reviewer's context.
            builder.Append("narrowed to: ");
            AppendClamped(builder, string.Join(", ", changeSet.Paths), cap.FieldChars);
            builder.Append(" (only these paths were read; changes outside them are not part of this review)\n");
        }

        builder.Append('\n');
        AppendCommits(builder, changeSet.Commits, changeSet.CommitsTruncated, cap);
        AppendFiles(builder, changeSet, cap);

        builder.Append("\nDiff:\n");
        if (diff.Length == 0)
        {
            builder.Append("No tracked change is in the diff.\n");
        }
        else
        {
            var shown = cut ? DiffCutLength(diff, cap.DiffChars) : diff.Length;
            builder.Append(diff, 0, shown);
            if (diff[shown - 1] != '\n')
            {
                builder.Append('\n');
            }

            if (cut)
            {
                builder.Append("[the diff was cut at ").Append(shown).Append(" of ").Append(diff.Length)
                    .Append(" characters, so it is incomplete: read the rest with ");
                AppendGitDiffHint(builder, changeSet, statOnly: false);
                builder.Append("]\n");
            }
            else if (changeSet.DiffTruncated)
            {
                builder.Append("[the diff is incomplete: it was truncated before this package was built; read the rest with ");
                AppendGitDiffHint(builder, changeSet, statOnly: false);
                builder.Append("]\n");
            }
        }

        builder.Append('\n').Append(UntrackedNote).Append('\n');

        if (!string.IsNullOrWhiteSpace(brief.Focus))
        {
            builder.Append("\nWhat to concentrate on:\n");
            AppendClamped(builder, brief.Focus, cap.FieldChars);
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

    /// <summary>One line per commit, newest first, within its own line budget, and honest about what was left out.</summary>
    private static void AppendCommits(StringBuilder builder, IReadOnlyList<ReviewCommit> commits, bool truncated, ReviewPackageLimits cap)
    {
        if (commits.Count == 0)
        {
            builder.Append("no commits in this change set\n");
            return;
        }

        var written = Math.Min(commits.Count, cap.CommitLines);
        for (var index = 0; index < written; index++)
        {
            var commit = commits[index];
            builder.Append(ShortSha(commit.Sha)).Append(' ');
            AppendClamped(builder, commit.Subject, cap.FieldChars);
            builder.Append('\n');
        }

        if (truncated)
        {
            builder.Append("... and more commits than these ")
                .Append(written.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(" (the list stops at the source's cap)\n");
        }
        else if (written < commits.Count)
        {
            builder.Append("... and ").Append(commits.Count - written).Append(" more commits\n");
        }
    }

    /// <summary>One line per file, in git's order, within its own line budget.</summary>
    private static void AppendFiles(StringBuilder builder, ReviewChangeSet changeSet, ReviewPackageLimits cap)
    {
        var files = changeSet.Files;
        if (files.Count == 0)
        {
            builder.Append("\nFiles: none\n");
            return;
        }

        builder.Append("\nFiles:\n");
        var written = Math.Min(files.Count, cap.FileLines);
        for (var index = 0; index < written; index++)
        {
            var file = files[index];
            builder.Append(file.Status).Append(' ').Append(file.Path);
            if (file.IsBinary)
            {
                builder.Append(" (binary)");
            }

            builder.Append('\n');
        }

        if (written < files.Count)
        {
            builder.Append("... and ").Append(files.Count - written).Append(" more files (list them with ");
            AppendGitDiffHint(builder, changeSet, statOnly: true);
            builder.Append(")\n");
        }
    }

    /// <summary>
    /// How to read more of this change set with <c>git_diff</c>, with the arguments that reproduce it. A bare
    /// <c>git_diff</c> compares the repository's default branch, so after a review against another base (or of one step's
    /// commit) it would show a different change set and the reviewer would judge changes it was never asked about.
    /// <c>git_diff</c> reads <c>base...HEAD</c> plus the uncommitted tree: with the merge-base this package was built from
    /// that is exactly the branch change set, while for a single commit it is that commit and everything after it.
    /// </summary>
    private static void AppendGitDiffHint(StringBuilder builder, ReviewChangeSet changeSet, bool statOnly)
    {
        var baseSha = string.IsNullOrWhiteSpace(changeSet.Base) ? null : changeSet.Base.Trim();
        if (changeSet.SingleCommit && baseSha is null)
        {
            builder.Append("the read tool on the listed files: git_diff cannot isolate a root commit");
            return;
        }

        builder.Append("git_diff {");
        if (baseSha is not null)
        {
            builder.Append("\"base\": \"").Append(JsonEncodedText.Encode(baseSha).Value).Append('"');
            if (statOnly)
            {
                builder.Append(", ");
            }
        }

        if (statOnly)
        {
            builder.Append("\"stat_only\": true");
        }

        builder.Append('}');
        if (!statOnly)
        {
            builder.Append(", adding paths to narrow it or \"stat_only\": true for the file list alone");
        }

        if (changeSet.SingleCommit)
        {
            builder.Append(" - that range also holds every commit after ").Append(ShortSha(changeSet.HeadCommit))
                .Append(" and the uncommitted tree, so judge only what this commit changed");
        }
        else if (baseSha is not null)
        {
            builder.Append(" - that base reproduces exactly this change set; without it git_diff compares the default branch instead");
        }
    }

    private static void AppendField(StringBuilder builder, string label, string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append(label);
        AppendClamped(builder, value, limit);
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

    /// <summary>
    /// Where the diff stops: the last line boundary at or before the cap, or the cap itself when there is none - moved back
    /// one character when that would split a surrogate pair, so the package never carries half a character.
    /// </summary>
    private static int DiffCutLength(string diff, int maxChars)
    {
        var cut = diff.LastIndexOf('\n', maxChars - 1, maxChars);
        if (cut > 0)
        {
            return cut;
        }

        return char.IsHighSurrogate(diff[maxChars - 1]) ? maxChars - 1 : maxChars;
    }
}
