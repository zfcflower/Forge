using System.Globalization;
using System.Text;

namespace Forge.IO;

/// <summary>Linux: freedesktop.org trash. <c>files/</c> receives the item, <c>info/&lt;name&gt;.trashinfo</c> records
/// where it came from. The info file is created exclusively first, which reserves the name; a collision gets a
/// numeric suffix. A move that cannot be done by rename (another volume) fails; it is never turned into a delete.</summary>
public sealed class XdgTrash : ITrashService
{
    private readonly string filesDirectory;
    private readonly string infoDirectory;
    private readonly Func<DateTime> clock;

    /// <param name="trashRoot">The <c>Trash</c> directory (normally <c>$XDG_DATA_HOME/Trash</c>).</param>
    /// <param name="clock">Local time source for <c>DeletionDate</c>; tests inject a fixed one.</param>
    public XdgTrash(string trashRoot, Func<DateTime>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trashRoot);
        filesDirectory = Path.Combine(trashRoot, "files");
        infoDirectory = Path.Combine(trashRoot, "info");
        this.clock = clock ?? (() => DateTime.Now);
    }

    /// <summary><c>$XDG_DATA_HOME/Trash</c>; an unset, empty or relative <c>XDG_DATA_HOME</c> falls back to
    /// <c>&lt;home&gt;/.local/share</c> as the specification says.</summary>
    public static XdgTrash FromEnvironment(Func<string, string?> getVariable, string home, Func<DateTime>? clock = null)
    {
        var data = getVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(data) || !Path.IsPathRooted(data))
        {
            data = Path.Combine(home, ".local", "share");
        }

        return new XdgTrash(Path.Combine(data, "Trash"), clock);
    }

    public Task MoveToTrashAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        return Task.Run(() => Move(fullPath), CancellationToken.None);
    }

    /// <summary>The <c>Path=</c> value: each segment of the absolute path percent-encoded as UTF-8, separators kept.</summary>
    public static string EncodePath(string absolutePath)
    {
        var segments = absolutePath.Replace('\\', '/').Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }

        return string.Join('/', segments);
    }

    private void Move(string fullPath)
    {
        var isDirectory = TrashNames.Classify(fullPath);
        Directory.CreateDirectory(filesDirectory);
        Directory.CreateDirectory(infoDirectory);
        var name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var content = string.Create(CultureInfo.InvariantCulture,
            $"[Trash Info]\nPath={EncodePath(fullPath)}\nDeletionDate={clock():yyyy-MM-dd'T'HH:mm:ss}\n");
        for (var attempt = 1; ; attempt++)
        {
            var candidate = TrashNames.WithSuffix(name, attempt);
            var infoPath = Path.Combine(infoDirectory, candidate + ".trashinfo");
            var target = Path.Combine(filesDirectory, candidate);
            if (TrashNames.Occupied(target))
            {
                continue;
            }

            try
            {
                using var info = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                info.Write(new UTF8Encoding(false).GetBytes(content));
            }
            catch (IOException) when (File.Exists(infoPath))
            {
                continue;
            }

            try
            {
                if (isDirectory)
                {
                    Directory.Move(fullPath, target);
                }
                else
                {
                    File.Move(fullPath, target, overwrite: false);
                }

                return;
            }
            catch
            {
                try
                {
                    File.Delete(infoPath);
                }
                catch (IOException)
                {
                    // An orphan info file is harmless; the original failure is what matters.
                }

                throw;
            }
        }
    }
}
