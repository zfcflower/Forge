using Forge.Abstractions;

namespace Forge.Core.Agents;

/// <summary>
/// What one agent instance has seen of the files it read, held in memory for the life of the instance and never persisted:
/// <c>read</c> records the state it stat'ed before reading, <c>write</c> compares it before replacing an existing file,
/// <c>edit</c> and <c>append</c> move it forward only when the state they wrote over was still the one the agent had read.
/// <para>The record is per instance, not per session id: a rebuilt agent has not seen what its predecessor read, and the
/// context it inherits may hold text older than the disk. There is no cap and no eviction on purpose - dropping an entry
/// would make a file the agent already read suddenly demand a second read, while the memory grows only with the number of
/// distinct files one agent touched. Keys arrive normalized (<c>WorkspacePath.Normalize</c>), so the table only has to
/// apply the platform's case rule (the same one <c>PathGate</c> uses).</para>
/// </summary>
internal sealed class FileVersionLedger : IFileVersionLedger
{
    private readonly Dictionary<string, FileVersion> versions =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly Lock gate = new();

    public bool TryGet(string path, out FileVersion version)
    {
        lock (gate)
        {
            return versions.TryGetValue(path, out version);
        }
    }

    public void Record(string path, FileVersion version)
    {
        lock (gate)
        {
            versions[path] = version;
        }
    }
}
