---
name: codemode
description: >-
  Use when a fact has to be computed across many files or many tools — an inventory of something structural, a count, a
  grouping, a joined list, a drift check, a fan-out whose next path comes from the previous result — so that one
  run_code script replaces a dozen reads and only its answer reaches the conversation. Do not use it for reading one
  file or running one search (the typed tools are cheaper), and not for anything that needs a shell, the network, the
  environment, a process or a file write: a script cannot do those. If `run_code` is not in your tool list, this skill
  does not apply.
---

# CodeMode: one script instead of a dozen calls

`run_code` runs one C# script in a separate process and returns its value once. It is the only entry that can put
**control flow between tool calls**: loop, branch, and compute the next argument from the previous result. Independent
calls in one message already run in parallel, so a script is never the way to save a round trip on unrelated reads —
it is the way to save turns when the *shape* of the remaining work depends on what came back.

## When a script is the right tool

| Shape | What it looks like |
|---|---|
| Enumerate, then compute | every implementation of an interface with its side effect, grouped and sorted; a count per module; the schema fields of every tool |
| Join across files | which spec numbers no longer match the constants they describe; every caller of a changed member plus the tests that name it |
| Data-dependent fan-out | a search returns `path:line`; read *that* offset in each file and return only the matching signature |
| Filter before context | scan two hundred files, return only those whose hit count is above five |
| Orchestrate non-file tools | three MCP read-only tools and a web fetch, intersected on one field, twenty lines back |

## When it is not

- One file, one search, two or three independent calls: `read` takes `paths` and `pattern`, `grep` takes `files_only`,
  `max_per_file` and `total_chars` — they answer those in one cheap call, with no compile and no process.
- Shell, network, environment, processes, file writes: the script surface refuses all of them at compile time. That is
  the shell's job.
- Large logs and binary formats: a script cannot open a file at all; it reads what the tools return, at most 20 000
  characters per inner call.

## The surface

Callable inside a script: **only tools your own agent holds *and* that belong to the script surface** — `read`, `ls`,
`find`, `grep`, `web_fetch`, `web_search`, the read-only MCP adapters, plugin tools. A sub-agent holds less than the
root, so the same script can see fewer tools there. Never callable, whatever the agent holds: `write`, `edit`, `copy`,
`move`, `delete`, `bash`, the git and pull-request tools, the extension authoring tools. `Tools.List()` is the
authority — a name outside it throws, and a script has no partial success.

API: `Tools.List()`, `Tools.Describe("name")`, `await Tools.Call("name", new { … })`, `Tools.Json<T>(text)`,
`Log(value)`. Limits: 60 s by default (600 s at most), 8 inner calls in flight, 20 000 characters per inner result,
40 000 returned.

## Recipes

**Inventory** — one line per item, each carrying `path:line`:

```csharp
var hits = await Tools.Call("grep", new { pattern = ": ITool", path = "src", glob = "*.cs" });
var at = Regex.Matches(hits, @"^([^\r\n:]+):(\d+):", RegexOptions.Multiline)
    .Select(m => (Path: m.Groups[1].Value, Line: int.Parse(m.Groups[2].Value)))
    .DistinctBy(x => (x.Path, x.Line));
var rows = new List<string>();
foreach (var (path, line) in at)
{
    var slice = await Tools.Call("read", new { path, offset = line, limit = 60 });
    var side = Regex.Match(slice, @"ToolSideEffect\.(\w+)");
    rows.Add($"{path}:{line} {(side.Success ? side.Groups[1].Value : "?")}");
}

return string.Join('\n', rows.OrderBy(row => row, StringComparer.Ordinal));
```

**Drift check** — return only the mismatches, never the agreement:

```csharp
var declared = await Tools.Call("grep", new { pattern = "DefaultGrepMaxResults|MaxReadMaxLines", path = "src", regex = true });
var spec     = await Tools.Call("read", new { path = "src/Forge.IO/SPEC.md", pattern = "上限", context = 2 });
var numbers  = Regex.Matches(declared, @"= ([\d_]+)").Select(m => m.Groups[1].Value.Replace("_", "")).Distinct();
return string.Join('\n', numbers.Where(n => !spec.Contains(n, StringComparison.Ordinal)).Select(n => $"SPEC silent on {n}"));
```

**Fan-out with a computed offset** — the case no typed call can express:

```csharp
var hits = await Tools.Call("grep", new { pattern = "ApprovalRequest", path = "src", glob = "*.cs", limit = 200 });
var per = Regex.Matches(hits, @"^([^\r\n:]+):(\d+):", RegexOptions.Multiline)
    .GroupBy(m => m.Groups[1].Value)
    .Take(8)
    .Select(group => (Path: group.Key, Offset: Math.Max(1, int.Parse(group.First().Groups[2].Value) - 4)));
var excerpts = await Task.WhenAll(per.Select(x => Tools.Call("read", new { path = x.Path, offset = x.Offset, limit = 20 })));
return string.Join("\n---\n", excerpts);
```

## Rules that keep the answer usable

1. **Always return `path:line` next to a conclusion.** A summary without a location cannot be checked, so the caller
   reads everything again and the script saved nothing.
2. **Return the smallest thing that answers the question.** The script may read everything; the conversation pays for
   what comes back.
3. **Keep it short.** A compile or runtime error costs a whole turn, so prefer a straight loop over clever generics and
   split the work instead of nesting it.
4. **Say what you left out** (`… N more not returned`), the way the file tools do.
5. **Do not script what one call already answers** — that is how the tool stops being worth its own definition.
