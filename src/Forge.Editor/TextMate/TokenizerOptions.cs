// Governed by THIRD_PARTY_NOTICES.md: adapted from Tint (MIT, Sebastien Ros).

namespace Forge.Editor.TextMate;

// Settings a tokenizer uses while tokenizing one document.
public sealed class TokenizerOptions
{
    public static TokenizerOptions Default { get; } = new();

    // How long one line may take, checked between searches; null for no limit. A line that runs out of time ends with
    // the tokens found so far and StoppedEarly set.
    public TimeSpan? LineTimeBudget { get; init; }
}
