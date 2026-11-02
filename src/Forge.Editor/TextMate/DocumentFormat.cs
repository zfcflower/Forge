// Governed by THIRD_PARTY_NOTICES.md.

namespace Forge.Editor.TextMate;

// The formats grammars and themes are stored in.
public enum DocumentFormat
{
    // JSON when the content starts with an object, an array or a comment, an XML property list otherwise.
    Auto,

    // JSON, with comments and trailing commas allowed.
    Json,

    // An XML property list: .tmLanguage, .plist, .tmTheme.
    Plist,
}
