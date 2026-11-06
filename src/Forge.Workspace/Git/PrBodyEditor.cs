using System.Globalization;
using System.Text;

namespace Forge.Workspace;

/// <summary>One section replacement: the <c>## <paramref name="Heading"/></c> section gets <paramref name="Content"/>.</summary>
public sealed record PrSectionEdit(string Heading, string Content);

/// <summary>
/// Changes the title and body of a pull request without trusting what was read earlier. Every edit is read, compute,
/// re-read, write, read back: the second read is taken immediately before the write, so a change somebody else made in
/// the meantime is noticed (a section edit is then applied onto the newest body, a full replacement is refused with both
/// versions shown), and the read back after the write proves the platform stored what was meant. A read back that
/// differs is an error, never silently accepted. Used by <c>pr_edit</c> and by the screenshot step for the
/// <c>## Screenshots</c> section. The platform has no compare-and-swap, so the window between the second read and the
/// write is narrow but not zero; the read back is what closes it for the caller.
/// </summary>
public sealed class PrBodyEditor(IPrPlatform platform)
{
    /// <summary>GitHub rejects a pull request body longer than this many characters.</summary>
    public const int MaxBodyLength = 65536;

    private const int ShownCharacters = 4000;

    /// <summary>Replaces one <c>## heading</c> section of the current branch's pull request, leaving the rest of the body alone.</summary>
    public Task<PrResult<PullRequestInfo>> ReplaceSectionAsync(
        string workingDirectory,
        string heading,
        string content,
        CancellationToken ct = default) =>
        EditAsync(workingDirectory, null, null, new PrSectionEdit(heading, content), null, ct);

    /// <summary>Replaces the whole body; refused when the body changed on the platform while the edit was prepared.</summary>
    public Task<PrResult<PullRequestInfo>> ReplaceBodyAsync(
        string workingDirectory,
        string body,
        CancellationToken ct = default) =>
        EditAsync(workingDirectory, null, body, null, null, ct);

    /// <summary>
    /// The general form. At least one of <paramref name="title"/>, <paramref name="body"/> and <paramref name="section"/>
    /// is required, and <paramref name="body"/> and <paramref name="section"/> exclude each other.
    /// <paramref name="selector"/> names the pull request (number, URL or branch); null means the current branch's.
    /// On success the value is the pull request as read back after the write, and the message says what was done.
    /// </summary>
    public async Task<PrResult<PullRequestInfo>> EditAsync(
        string workingDirectory,
        string? title,
        string? body,
        PrSectionEdit? section,
        string? selector = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (title is null && body is null && section is null)
        {
            throw new ArgumentException("Give a title, a body or a section to change.");
        }

        if (body is not null && section is not null)
        {
            throw new ArgumentException("A full body and a section edit exclude each other.");
        }

        if (title is not null && title.Trim().Length == 0)
        {
            throw new ArgumentException("A title must not be blank.", nameof(title));
        }

        if (section is not null && section.Heading.Trim().Trim('#').Trim().Length == 0)
        {
            throw new ArgumentException("A section heading is required.", nameof(section));
        }

        title = title?.Trim();

        // Read 1: which pull request this is, and the state the edit is computed from.
        var first = await platform.ViewPullRequestAsync(workingDirectory, selector, ct).ConfigureAwait(false);
        if (!first.Success || first.Value is null)
        {
            return first;
        }

        var number = first.Value.Number;
        var newBody = body ?? (section is null ? null : PrBodyRenderer.ReplaceSection(first.Value.Body, section.Heading, section.Content));
        if (newBody is not null && newBody.Length > MaxBodyLength)
        {
            return PrResult<PullRequestInfo>.Fail(
                GitRemoteFailureKind.Other,
                $"The new body is {newBody.Length.ToString(CultureInfo.InvariantCulture)} characters; the platform accepts at most " +
                $"{MaxBodyLength.ToString(CultureInfo.InvariantCulture)}. Shorten it and call pr_edit again.");
        }

        // Read 2: immediately before the write, by number so a branch rename or a second pull request cannot confuse it.
        var numberText = number.ToString(CultureInfo.InvariantCulture);
        var second = await platform.ViewPullRequestAsync(workingDirectory, numberText, ct).ConfigureAwait(false);
        if (!second.Success || second.Value is null)
        {
            return second;
        }

        var current = second.Value;
        var merged = false;
        if (!SameText(first.Value.Body, current.Body))
        {
            if (body is not null && !SameText(body, current.Body))
            {
                return PrResult<PullRequestInfo>.Fail(
                    GitRemoteFailureKind.Other,
                    "The pull request body changed on the platform between reading it and writing, and replacing the whole body " +
                    "would discard that change. Nothing was written.\n" +
                    "--- current body (just read) ---\n" + Cut(current.Body) + "\n" +
                    "--- your replacement ---\n" + Cut(body) + "\n" +
                    "Decide how to combine them (a section edit merges onto the newest body by itself) and call pr_edit again.");
            }

            if (section is not null)
            {
                newBody = PrBodyRenderer.ReplaceSection(current.Body, section.Heading, section.Content);
                merged = true;
                if (newBody.Length > MaxBodyLength)
                {
                    return PrResult<PullRequestInfo>.Fail(
                        GitRemoteFailureKind.Other,
                        "The body changed on the platform while you were editing, and the section edit no longer fits the " +
                        $"{MaxBodyLength.ToString(CultureInfo.InvariantCulture)} character limit on top of it. Nothing was written.");
                }
            }
        }

        if (title is not null && !SameText(first.Value.Title, current.Title) && !SameText(title, current.Title))
        {
            return PrResult<PullRequestInfo>.Fail(
                GitRemoteFailureKind.Other,
                "The pull request title changed on the platform between reading it and writing. Nothing was written.\n" +
                $"Current title: {current.Title}\nYour title: {title}\n" +
                "Decide which one is right and call pr_edit again.");
        }

        var titleChanges = title is not null && !SameText(title, current.Title);
        var bodyChanges = newBody is not null && !SameText(newBody, current.Body);
        if (!titleChanges && !bodyChanges)
        {
            return PrResult<PullRequestInfo>.Ok(
                current,
                $"PR #{numberText} already has that title and body; nothing was written.");
        }

        var edited = await platform.EditPullRequestAsync(
            workingDirectory,
            number,
            titleChanges ? title : null,
            bodyChanges ? newBody : null,
            ct).ConfigureAwait(false);
        if (!edited.Success)
        {
            return PrResult<PullRequestInfo>.Fail(
                edited.Status.Failure,
                edited.Status.Message,
                edited.Status.StandardError,
                edited.Reason);
        }

        // Read 3: the edit only counts once the platform shows it.
        var third = await platform.ViewPullRequestAsync(workingDirectory, numberText, ct).ConfigureAwait(false);
        if (!third.Success || third.Value is null)
        {
            return PrResult<PullRequestInfo>.Fail(
                third.Status.Failure,
                "The edit was sent, but reading the pull request back failed, so it is not verified: " + third.Status.Message,
                third.Status.StandardError,
                third.Reason);
        }

        var mismatches = new List<string>(2);
        if (titleChanges && !SameText(title, third.Value.Title))
        {
            mismatches.Add($"title (wrote '{title}', read back '{third.Value.Title}')");
        }

        if (bodyChanges && !SameText(newBody, third.Value.Body))
        {
            mismatches.Add(
                $"body (wrote {newBody!.Length.ToString(CultureInfo.InvariantCulture)} characters, read back " +
                $"{third.Value.Body.Length.ToString(CultureInfo.InvariantCulture)})");
        }

        if (mismatches.Count > 0)
        {
            return PrResult<PullRequestInfo>.Fail(
                GitRemoteFailureKind.Other,
                $"Verification failed for PR #{numberText}: after the write the platform shows something else for the " +
                string.Join(" and the ", mismatches) + ". Someone may have edited it again, or the platform altered the text. " +
                "Read it with pr_view and repeat the edit if it is still wanted.");
        }

        var done = new StringBuilder(160);
        done.Append("Updated PR #").Append(numberText).Append(" (").Append(third.Value.Url).Append("): ");
        var parts = new List<string>(2);
        if (titleChanges)
        {
            parts.Add("title");
        }

        if (bodyChanges)
        {
            parts.Add(section is not null ? $"section '{section.Heading.Trim().Trim('#').Trim()}'" : "body");
        }

        done.Append(string.Join(" and ", parts)).Append(" changed; read back and verified.");
        if (merged)
        {
            done.Append(" The body had changed on the platform while you were editing; your section edit was applied onto the newest body.");
        }

        return PrResult<PullRequestInfo>.Ok(third.Value, done.ToString());
    }

    /// <summary>Texts compare equal when they differ only in line endings and trailing whitespace.</summary>
    public static bool SameText(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    private static string Normalize(string? text) =>
        (text ?? string.Empty).ReplaceLineEndings("\n").TrimEnd();

    private static string Cut(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= ShownCharacters
            ? trimmed
            : trimmed[..ShownCharacters] + "\n... [cut after " + ShownCharacters.ToString(CultureInfo.InvariantCulture) + " of " +
              trimmed.Length.ToString(CultureInfo.InvariantCulture) + " characters]";
    }
}
