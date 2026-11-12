using Forge.Abstractions;

namespace Forge.Core.Prompts.Templates;

/// <summary>
/// The slash commands Forge ships. Each is an ordinary command template: the user can override one by saving a
/// same-id file under <c>~/.forge/commands</c> or <c>&lt;workspace&gt;/.forge/commands</c>, or extend it with
/// <c>mode: append</c>. Bodies use <c>$ARGUMENTS</c> (see <see cref="CommandExpander"/>).
/// </summary>
public static class BuiltInCommands
{
    public static BuiltInPromptTemplate Review { get; } = new(
        PromptTemplateKind.Command,
        "review",
        "review",
        "Review the uncommitted changes (or the given paths) and report material findings.",
        """
        Review the current uncommitted changes in this worktree. $ARGUMENTS

        Read the diff and the code around it, and check it against the repository's specs. Report correctness bugs, broken contracts, missed call sites, unhandled failure or cancellation, and missing tests. Most severe first, each with file, line and the concrete fix. Do not edit any file.
        """,
        [],
        ArgumentHint: "[paths or focus]");

    public static BuiltInPromptTemplate Explain { get; } = new(
        PromptTemplateKind.Command,
        "explain",
        "explain",
        "Explain how a piece of the codebase works.",
        """
        Explain how this works in this repository: $ARGUMENTS

        Read the relevant code first. Cover the entry points, the data flow and the important invariants, citing files and lines. Do not change anything.
        """,
        [],
        ArgumentHint: "<file, symbol or feature>");

    public static BuiltInPromptTemplate Tests { get; } = new(
        PromptTemplateKind.Command,
        "tests",
        "tests",
        "Write or extend tests for the given code.",
        """
        Write tests for: $ARGUMENTS

        Follow the test conventions already in the repository. Cover the main path, the edge cases, and the failure and cancellation paths that matter. Run the new tests and report what they showed.
        """,
        [],
        ArgumentHint: "<file, symbol or behaviour>");

    public static BuiltInPromptTemplate Fix { get; } = new(
        PromptTemplateKind.Command,
        "fix",
        "fix",
        "Diagnose a problem, fix the root cause and verify it.",
        """
        Diagnose and fix: $ARGUMENTS

        Reproduce or locate the problem first, find the root cause rather than the symptom, make the smallest fix, and verify it with the project's own build and tests.
        """,
        [],
        ArgumentHint: "<error, failing test or symptom>");

    public static IReadOnlyList<BuiltInPromptTemplate> All { get; } = [Review, Explain, Tests, Fix];
}
