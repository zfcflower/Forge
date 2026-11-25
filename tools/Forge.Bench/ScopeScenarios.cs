using Forge.Abstractions;
using Forge.IO.Tools;

namespace Forge.Bench;

/// <summary>
/// The approval boundary one write or execution call costs: <c>ToolScopes.Of</c> parses the call's arguments and answers
/// "inside" or "outside" before the policy runs, so every such call pays one parse on top of the parse the tool does itself.
/// The step did not exist before the workspace-boundary change, so the row has no BCL baseline - what it reports is the whole
/// added cost, not a ratio. A read-only call skips it (the policy allows it first), which is why no read row exists here.
/// </summary>
internal static class ScopeScenarios
{
    public static IEnumerable<BenchScenario> Create(Fixtures fixtures)
    {
        yield return Write(fixtures, "1KiB", 1 << 10);
        yield return Write(fixtures, "1MiB", 1 << 20);
        yield return Shell(fixtures);
    }

    private static BenchScenario Write(Fixtures fixtures, string label, int byteCount)
    {
        return new BenchScenario(
            $"approval.scope.write.{label}",
            "approval",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var tool = new WriteTool();
                var workspace = fixtures.Workspace.Root;
                var json = $$"""{"path":"src/a.txt","content":"{{new string('x', byteCount)}}"}""";
                var call = new ToolCall("call", "write", json);
                return Task.FromResult(new BenchCase
                {
                    Run = _ => Task.FromResult(new OpResult(json.Length, Items: 1, Signature: (long)Scope(tool, call, workspace))),
                    Note = "one WriteTool call's boundary: JSON parse of 'path' plus the containment test",
                });
            });
    }

    private static BenchScenario Shell(Fixtures fixtures)
    {
        return new BenchScenario(
            "approval.scope.bash",
            "approval",
            Heavy: false,
            PrepareAsync: _ =>
            {
                var tool = new BashTool(fixtures.Runner);
                var workspace = fixtures.Workspace.Root;
                var json = """{"command":"dotnet build src/Forge.IO/Forge.IO.csproj -c Release && git status --short"}""";
                var call = new ToolCall("call", "bash", json);
                return Task.FromResult(new BenchCase
                {
                    Run = _ => Task.FromResult(new OpResult(json.Length, Items: 1, Signature: (long)Scope(tool, call, workspace))),
                    Note = "one bash call's boundary: argument fragments of the command line plus the workdir",
                });
            });
    }

    private static ToolCallScope Scope(ITool tool, ToolCall call, string workspace) => ToolScopes.Of(tool, call, workspace);
}
