using System.Collections.ObjectModel;
using Forge.App.Localization;

namespace Forge.App.ViewModels;

/// <summary>
/// One round of the composer's Chat History: the user's message and the reply it got, as one block. A block is built once per
/// open of the flyout, so its relative age is resolved when the list is built rather than on every repaint.
/// </summary>
internal sealed class ComposerHistoryEntry(string text, string reply, DateTimeOffset at, int roundIndex)
{
    /// <summary>The user's message exactly as it was written: what the block hands back to the composer when clicked.</summary>
    public string Text { get; } = text;

    /// <summary>
    /// The message as the row shows it: one line, so its own line breaks and runs of spaces collapse to single spaces (the
    /// block is a list entry, not a preview - the tooltip carries the message's own shape).
    /// </summary>
    public string DisplayText { get; } = Collapse(text);

    /// <summary>The answer's text, all of the round's answer blocks joined and flattened the same way; at most two lines.</summary>
    public string Reply { get; } = Collapse(reply);

    /// <summary>Whether the round got any answer text at all (a stopped or still-running round may have none).</summary>
    public bool HasReply => Reply.Length > 0;

    /// <summary>The age shown at the block's right end ("3 分钟前", "昨天 14:32").</summary>
    public string Time { get; } = ComposerHistoryStrings.Relative(at);

    /// <summary>The transcript round the block lives in: what "locate the message" scrolls back to.</summary>
    internal int RoundIndex { get; } = roundIndex;

    /// <summary>Newlines, tabs and runs of spaces become one space; leading and trailing whitespace goes.</summary>
    internal static string Collapse(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Whether the filter matches this block: the user's words or the answer's.</summary>
    internal bool Matches(string search) =>
        search.Length == 0 ||
        Text.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        Reply.Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The composer's Chat History: one block per round - the message the user typed and the answer it got - newest last,
/// filterable as the user types. It is rebuilt from the transcript every time the flyout opens: the transcript is the one
/// place that knows which rounds exist, and a session being read backwards from the store grows it a page at a time, so the
/// list is never a second copy that can drift.
/// </summary>
internal sealed class ComposerHistoryViewModel : NotifyingObject
{
    private readonly Func<TranscriptViewModel?> transcript;
    private readonly List<ComposerHistoryEntry> all = [];
    private string search = string.Empty;

    /// <param name="transcript">The transcript to read on <see cref="Refresh"/>; null while the session has none.</param>
    public ComposerHistoryViewModel(Func<TranscriptViewModel?> transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        this.transcript = transcript;
    }

    /// <summary>The blocks on screen: <see cref="Search"/> applied to every round of the session.</summary>
    public ObservableCollection<ComposerHistoryEntry> Entries { get; } = [];

    /// <summary>The filter field's text; matching is a case-insensitive substring test over the message and its reply.</summary>
    public string Search
    {
        get => search;
        set
        {
            if (Set(ref search, value ?? string.Empty))
            {
                Apply();
            }
        }
    }

    /// <summary>The session has no messages at all (the flyout's empty state).</summary>
    public bool IsEmpty => all.Count == 0;

    /// <summary>The session has messages but the filter matches none of them.</summary>
    public bool HasNoMatch => all.Count > 0 && Entries.Count == 0;

    /// <summary>
    /// Rebuilds the list from the transcript: one block per round that a user message opened, carrying that message and the
    /// text of the answer below it. A round with no user message (a session receipt, a notice) is not a history block: there
    /// is no message to take back.
    /// </summary>
    public void Refresh()
    {
        all.Clear();
        if (transcript() is { } t)
        {
            for (var index = 0; index < t.Rounds.Count; index++)
            {
                var round = t.Rounds[index];
                if (round.Rows.Count == 0 || round.Rows[0] is not UserMessageViewModel { HasText: true } user)
                {
                    continue;
                }

                all.Add(new ComposerHistoryEntry(user.Text, ReplyOf(round), user.At, index));
            }
        }

        Apply();
    }

    /// <summary>
    /// The round's answer as one text: every reply block in order (a turn that called tools writes several), separated by a
    /// blank line the way the transcript reads.
    /// </summary>
    private static string ReplyOf(TranscriptRoundViewModel round)
    {
        if (round.Card is not { } card)
        {
            return string.Empty;
        }

        var reply = new List<string>();
        foreach (var block in card.Blocks)
        {
            if (block is TurnTextViewModel { CopyText.Length: > 0 } text)
            {
                reply.Add(text.CopyText);
            }
        }

        return string.Join("\n\n", reply);
    }

    private void Apply()
    {
        Entries.Clear();
        foreach (var entry in all)
        {
            if (entry.Matches(search))
            {
                Entries.Add(entry);
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatch));
    }
}
