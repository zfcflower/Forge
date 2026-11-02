// Adapted from Tint (https://github.com/sebastienros/tint, MIT License, Copyright (c) 2026 Sebastien Ros):
// see THIRD_PARTY_NOTICES.md for the license text and the sources it was itself derived from.

namespace Forge.Editor.TextMate.Grammars;

/// <summary>The coarse token classification VS Code keeps in the encoded token attributes.</summary>
internal enum StandardTokenType
{
    /// <summary>Code.</summary>
    Other = 0,

    /// <summary>A comment.</summary>
    Comment = 1,

    /// <summary>A string literal.</summary>
    String = 2,

    /// <summary>A regular expression literal.</summary>
    RegEx = 3,
}
