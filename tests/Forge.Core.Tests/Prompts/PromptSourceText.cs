using Forge.Core.Prompts;

namespace Forge.Core.Tests.Prompts;

/// <summary>
/// Prompt text that is embedded in a source literal carries whatever line endings git checked the file out with
/// (CRLF on a Windows checkout), while <see cref="PromptComposer"/> canonicalizes every layer to <c>\n</c>. Tests
/// compare the two by content, not by checkout artifacts.
/// </summary>
internal static class PromptSourceText
{
    public static string Lf(string text) => text.ReplaceLineEndings("\n");
}
