using System.Text;
using Forge.App.Composition;
using Forge.IO;
using Forge.Mcp;

namespace Forge.App.Services;

/// <summary>One <c>mcp.json</c> of the two Forge reads, with the entries exactly as that file spells them.</summary>
internal sealed record McpServerFile(
    string Path,
    bool IsProject,
    IReadOnlyList<McpServerEntry> Entries,
    IReadOnlyList<McpConfigDiagnostic> Diagnostics);

/// <summary>What the settings page shows before anything is changed: both files, their entries and their problems.</summary>
internal sealed record McpConfigSnapshot(
    IReadOnlyList<McpServerFile> Files,
    IReadOnlyList<McpConfigDiagnostic> Diagnostics);

/// <summary>
/// The file side of MCP configuration: the global <c>~/.forge/mcp.json</c>, the project's <c>&lt;workspace&gt;/.forge/mcp.json</c>,
/// and the read–merge–write cycle over one of them. Writing takes <see cref="SettingsFileMutex"/> for the path and replaces the
/// file atomically, the same rules <c>NetworkSettingsStore</c> follows — an atomic replace makes the file valid, the mutex makes
/// the merge correct.
/// <para>Every method reads the file as it is on disk right before it writes it, so an edit made by hand (or by another tool)
/// between two saves is not silently dropped. The text itself comes from <see cref="McpConfigFile"/>, which never expands a
/// <c>${VAR}</c> placeholder and never rewrites an entry it was not asked to touch.</para>
/// <para>Pure file work: no threading, no cancellation; the caller runs it on the thread pool.</para>
/// </summary>
internal sealed class McpConfigStore
{
    private readonly string globalPath;
    private readonly string? projectPath;

    public McpConfigStore(string globalPath, string? workspaceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalPath);
        this.globalPath = Path.GetFullPath(globalPath);
        projectPath = string.IsNullOrWhiteSpace(workspaceDirectory)
            ? null
            : Path.GetFullPath(McpConfigLoader.ProjectPath(workspaceDirectory));
    }

    /// <summary>The user-level file; always a valid write target (its directory is created on the first save).</summary>
    public string GlobalPath => globalPath;

    /// <summary>The project's file; null when the app has no workspace directory.</summary>
    public string? ProjectPath => projectPath;

    /// <summary>Both files, the project's after the global one (the runtime config lets a project entry win by name).</summary>
    public McpConfigSnapshot Read()
    {
        var files = new List<McpServerFile>(2);
        var diagnostics = new List<McpConfigDiagnostic>(2);
        foreach (var (path, isProject) in Targets())
        {
            var file = ReadFile(path, isProject);
            files.Add(file);
            diagnostics.AddRange(file.Diagnostics);
        }

        return new McpConfigSnapshot(files, diagnostics);
    }

    /// <summary>
    /// The runtime config: both files parsed the way the servers need it (a <c>${VAR}</c> expanded, an unset one reported), a
    /// project entry replacing the global one of the same name. Read fresh from disk, so a save is never applied to a stale
    /// snapshot.
    /// </summary>
    public McpConfig Load()
    {
        var servers = new List<McpServerConfig>(4);
        var diagnostics = new List<McpConfigDiagnostic>(2);
        foreach (var (path, _) in Targets())
        {
            string json;
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                json = File.ReadAllText(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                diagnostics.Add(new McpConfigDiagnostic(path, null, "Could not read the file: " + exception.Message));
                continue;
            }

            var parsed = McpConfigLoader.Parse(json, path);
            foreach (var server in parsed.Servers)
            {
                servers.RemoveAll(existing => string.Equals(existing.Name, server.Name, StringComparison.Ordinal));
                servers.Add(server);
            }

            diagnostics.AddRange(parsed.Diagnostics);
        }

        return new McpConfig(servers, diagnostics);
    }

    /// <summary>
    /// Adds or replaces one entry in one file; the other file and every other entry stay as they are. A rename (the entry's
    /// name differs from <paramref name="replacedName"/>) removes the old key from that same file in the same write.
    /// </summary>
    public void Upsert(string path, McpServerEntry entry, string? replacedName = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Write(path, text => McpConfigFile.Upsert(text, entry, replacedName));
    }

    /// <summary>Writes entries read from another application's file, placeholders and unknown fields included.</summary>
    public void Import(string path, IReadOnlyList<McpServerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        Write(path, text => McpConfigFile.Import(text, entries));
    }

    /// <summary>Removes one entry from one file.</summary>
    public void Remove(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Write(path, text => McpConfigFile.Remove(text, name));
    }

    /// <summary>
    /// Sets an entry's <c>disabled</c> flag in the file it lives in; returns false when the entry is not in that file (the page
    /// reports it instead of inventing an entry). Every other field is read from the file and written back unchanged.
    /// </summary>
    public bool SetDisabled(string path, string name, bool disabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var changed = false;
        Write(path, text =>
        {
            var current = McpConfigFile.Read(text, path);
            McpServerEntry? found = null;
            foreach (var entry in current.Entries)
            {
                if (string.Equals(entry.Name, name, StringComparison.Ordinal))
                {
                    found = entry;
                    break;
                }
            }

            if (found is null)
            {
                // Nothing to change: leave the file alone instead of rewriting an identical copy.
                return null;
            }

            changed = true;
            return McpConfigFile.Upsert(text, found with { Disabled = disabled });
        });

        return changed;
    }

    private IEnumerable<(string Path, bool IsProject)> Targets()
    {
        yield return (globalPath, false);
        if (projectPath is not null)
        {
            yield return (projectPath, true);
        }
    }

    private static McpServerFile ReadFile(string path, bool isProject)
    {
        string? text = null;
        List<McpConfigDiagnostic>? problems = null;
        try
        {
            if (File.Exists(path))
            {
                text = File.ReadAllText(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problems = [new McpConfigDiagnostic(path, null, "Could not read the file: " + exception.Message)];
        }

        if (problems is not null)
        {
            return new McpServerFile(path, isProject, [], problems);
        }

        var read = McpConfigFile.Read(text, path);
        return new McpServerFile(path, isProject, read.Entries, read.Diagnostics);
    }

    /// <summary>
    /// Reads the file, lets <paramref name="transform"/> produce the new text and replaces the file under the path's mutex.
    /// A null result means "nothing to change" and leaves the file untouched. A file whose root is not a JSON object is refused
    /// rather than overwritten: it belongs to the user, not to this page.
    /// </summary>
    private static void Write(string path, Func<string?, string?> transform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(transform);
        var full = Path.GetFullPath(path);
        using (SettingsFileMutex.Acquire(full))
        {
            string? text = File.Exists(full) ? File.ReadAllText(full) : null;
            if (transform(text) is not { } updated)
            {
                return;
            }

            AtomicFile.WriteAllBytes(full, Encoding.UTF8.GetBytes(updated));
        }
    }
}
