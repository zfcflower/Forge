using System.Text;
using Forge.App.Services;

namespace Forge.App.Agents;

/// <summary>
/// What the chat can draw, told to the model: a model that does not know a fenced <c>mermaid</c> block becomes a diagram, or that
/// a local picture path becomes an image, writes ASCII art and bare paths instead. Like the orchestration block it is appended to
/// the <b>root</b> agent's role only (sub-agents report to their parent, not to the chat), and it is a pure function of the diagram
/// whitelist, so the same setting always yields the same bytes and the cached prompt prefix stays stable.
/// </summary>
internal static class ChatRenderingPrompt
{
    private const string Intro =
        """
        <chat-rendering>
        Your replies are shown as rich Markdown in a chat that draws these for you; prefer them to ASCII art or prose when they explain better.
        """;

    private const string DiagramHead =
        "- Diagrams: put the diagram source in a fenced block whose info string is `mermaid` and the chat renders it as a vector diagram. " +
        "Use one for flows, architectures, sequences, state machines, schemas and dependencies. One diagram per block, close the fence, " +
        "keep node labels short, and wrap a label that holds brackets, parentheses, quotes or punctuation in double quotes (`A[\"parse (v2)\"]`). " +
        "Do not use HTML, click/href callbacks or init directives. Enabled types: ";

    private const string DiagramTail =
        """
        .
        Example:
        ```mermaid
        flowchart TD
          A[User message] --> B{Needs a tool?}
          B -- yes --> C[Run tool] --> D[Reply]
          B -- no --> D
        ```
        A diagram that fails to render shows the user the error, who can send it back to you to fix; make the first try valid rather than decorative.
        """;

    private const string Rest =
        """
        - Pictures: `![alt text](absolute/or/workspace-relative/path.png)` shows a local image inline (PNG, JPEG, GIF, WebP, BMP). Remote images are never loaded, only linked. Use it for screenshots or charts you generated into a file.
        - Files: a path in backticks (`src/App.cs`) or a Markdown link opens the file when it exists: source files in the editor, PDF, HTML and other documents in the system application. Mention files that way so the user can open them.
        - Code: fenced blocks with a language tag, so they get a copy button. Show only the lines that matter.
        </chat-rendering>
        """;

    /// <summary>The block to append to the root agent's role.</summary>
    public static string Build()
    {
        var enabled = MermaidSettings.EnabledTypes();
        var builder = new StringBuilder(1400).Append(Intro).Append('\n');
        var any = false;
        foreach (var token in MermaidDiagramTypes.Tokens)
        {
            if ((MermaidDiagramTypes.Parse(token) & enabled) == 0)
            {
                continue;
            }

            if (!any)
            {
                builder.Append(DiagramHead);
            }
            else
            {
                builder.Append(", ");
            }

            builder.Append(Keyword(token));
            any = true;
        }

        // An empty whitelist leaves nothing to draw: say nothing about diagrams rather than promise ones that show as plain code.
        return (any ? builder.Append(DiagramTail).Append('\n') : builder).Append(Rest).ToString();
    }

    /// <summary>The header a model writes for a whitelist token; the common ones differ from their token.</summary>
    private static string Keyword(string token) => token switch
    {
        "sequence" => "sequenceDiagram",
        "state" => "stateDiagram-v2",
        "class" => "classDiagram",
        "er" => "erDiagram",
        "gitgraph" => "gitGraph",
        "treeview" => "treeView-beta",
        _ => token,
    };
}
