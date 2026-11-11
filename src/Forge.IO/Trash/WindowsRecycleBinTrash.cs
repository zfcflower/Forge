using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace Forge.IO;

/// <summary>
/// Windows Recycle Bin through <c>Microsoft.VisualBasic.FileIO.FileSystem</c> (no P/Invoke; verified under Native AOT
/// publish). The shell recycles an item only where a Recycle Bin can take it; elsewhere it deletes the item for good, and
/// with the shell's confirmations suppressed it does so silently. So two guards keep "never deletes" true:
/// <list type="bullet">
/// <item>A location without a Recycle Bin (a UNC path, a volume-GUID path, a network, removable, optical or RAM drive,
/// also when a link or junction on the way leads there) is refused up front, with nothing touched.</item>
/// <item>The shell's own dialogs stay on (<see cref="UIOption.AllDialogs"/>): an item the bin cannot take (larger than its
/// quota, the bin turned off for the drive) brings up the shell's "permanently delete?" warning, and cancelling it throws
/// <see cref="OperationCanceledException"/>, which is a failure here, not a silent success. The recycle confirmation itself
/// follows the user's Recycle Bin setting (off by default) and a long move shows its progress. The library makes the
/// process's main window (<see cref="Process.MainWindowHandle"/>) the dialogs' owner, so they open over Forge.</item>
/// </list>
/// A <c>subst</c> drive has no Recycle Bin of its own and reports itself as fixed, so a path on one is first translated to
/// the folder it maps (read from <c>subst.exe</c>, no P/Invoke) and that real path is recycled, into the real drive's bin.
/// </summary>
public sealed class WindowsRecycleBinTrash : ITrashService
{
    /// <summary>Links followed through one path before it is refused as a loop.</summary>
    private const int MaxLinkHops = 16;

    /// <summary>How long <c>subst.exe</c> may take to list the mappings before the path is used as it is.</summary>
    private static readonly TimeSpan SubstTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<string, DriveType> driveTypeOf;
    private readonly Func<IReadOnlyDictionary<char, string>> substMappings;

    public WindowsRecycleBinTrash()
        : this(static root => new DriveInfo(root).DriveType, ReadSubstMappings)
    {
    }

    /// <summary>For tests: the drive type of a path root (<c>C:\</c>) and the <c>subst</c> mappings (none by default).</summary>
    public WindowsRecycleBinTrash(Func<string, DriveType> driveTypeOf, Func<IReadOnlyDictionary<char, string>>? substMappings = null)
    {
        ArgumentNullException.ThrowIfNull(driveTypeOf);
        this.driveTypeOf = driveTypeOf;
        this.substMappings = substMappings ?? (static () => new Dictionary<char, string>());
    }

    public Task MoveToTrashAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ct.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        return Task.Run(() =>
        {
            var realPath = ResolveSubst(fullPath, substMappings());
            if (RecycleBinRefusal(realPath, driveTypeOf) is { } refusal)
            {
                throw new IOException(refusal);
            }

            if (Directory.Exists(realPath))
            {
                FileSystem.DeleteDirectory(realPath, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
            }
            else
            {
                FileSystem.DeleteFile(realPath, UIOption.AllDialogs, RecycleOption.SendToRecycleBin);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// <paramref name="fullPath"/> with a <c>subst</c> drive replaced by the folder it maps, repeatedly (a mapping may
    /// point into another substituted drive). A translation is kept only when it names something that exists while the
    /// original does: a mapping misread (say, a path in a code page the output could not carry) leaves the path as it was,
    /// where the shell's own warning still guards it.
    /// </summary>
    public static string ResolveSubst(string fullPath, IReadOnlyDictionary<char, string> mappings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(mappings);
        var current = fullPath;
        for (var hop = 0; hop < MaxLinkHops; hop++)
        {
            var root = Path.GetPathRoot(current);
            var drive = root is null ? null : DriveRoot(root);
            if (drive is null || !mappings.TryGetValue(char.ToUpperInvariant(drive[0]), out var target))
            {
                break;
            }

            var rest = current[root!.Length..];
            var translated = rest.Length == 0 ? target : Path.Join(target, rest);
            if (Exists(current) && !Exists(translated))
            {
                break;
            }

            current = translated;
        }

        return current;
    }

    /// <summary>
    /// Parses <c>subst.exe</c>'s listing: one <c>X:\: =&gt; C:\folder</c> line per mapping (the format is the same in
    /// every display language). Lines that do not have that shape are skipped.
    /// </summary>
    public static IReadOnlyDictionary<char, string> ParseSubstListing(string listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var mappings = new Dictionary<char, string>();
        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ');
            const string Arrow = ": => ";
            var arrow = line.IndexOf(Arrow, StringComparison.Ordinal);
            if (arrow != 2 && arrow != 3)
            {
                continue;
            }

            var drive = line[..arrow];
            var target = line[(arrow + Arrow.Length)..].Trim();
            if (drive.Length >= 2 && char.IsAsciiLetter(drive[0]) && drive[1] == ':' && Path.IsPathFullyQualified(target))
            {
                mappings[char.ToUpperInvariant(drive[0])] = target;
            }
        }

        return mappings;
    }

    /// <summary>
    /// The current <c>subst</c> mappings from <c>%SystemRoot%\System32\subst.exe</c>. When it cannot run, times out or
    /// fails, there are taken to be none: the path is then recycled as it is, guarded by the shell's own warning.
    /// </summary>
    public static IReadOnlyDictionary<char, string> ReadSubstMappings()
    {
        var empty = new Dictionary<char, string>();
        var subst = Path.Combine(Environment.SystemDirectory, "subst.exe");
        if (!File.Exists(subst))
        {
            return empty;
        }

        try
        {
            var start = new ProcessStartInfo(subst)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = OemEncoding(),
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return empty;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(SubstTimeout))
            {
                process.Kill();
                return empty;
            }

            return process.ExitCode == 0 ? ParseSubstListing(output.GetAwaiter().GetResult()) : empty;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Trace.WriteLine($"[trash] could not list the subst drives: {exception.Message}");
            return empty;
        }
    }

    /// <summary>The console (OEM) code page a console tool writes in; UTF-8 when it is not available.</summary>
    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    /// <summary>
    /// Why <paramref name="fullPath"/> cannot go to a Recycle Bin, or null: only a local fixed drive, named by its letter,
    /// has one. A UNC path (<c>\\server\share</c>, <c>\\?\UNC\…</c>), a volume-GUID path and any drive that is not
    /// <see cref="DriveType.Fixed"/> are refused, as is a drive whose type cannot be read. A link or junction anywhere on the
    /// path (the item itself included) is followed and its target judged the same way, since the shell works on the
    /// volume the link leads to.
    /// </summary>
    public static string? RecycleBinRefusal(string fullPath, Func<string, DriveType> driveTypeOf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(driveTypeOf);
        var pending = new Queue<string>();
        pending.Enqueue(fullPath);
        var hops = 0;
        while (pending.TryDequeue(out var current))
        {
            if (VolumeRefusal(fullPath, current, driveTypeOf) is { } refusal)
            {
                return refusal;
            }

            IReadOnlyList<string> targets;
            try
            {
                targets = LinkTargets(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return $"Cannot move '{fullPath}' to the Recycle Bin: the links on its path could not be read ({exception.Message}), and nothing was deleted.";
            }

            foreach (var target in targets)
            {
                if (++hops > MaxLinkHops)
                {
                    return $"Cannot move '{fullPath}' to the Recycle Bin: its path goes through more than {MaxLinkHops} links, and nothing was deleted.";
                }

                pending.Enqueue(target);
            }
        }

        return null;
    }

    /// <summary>The refusal for the volume of <paramref name="path"/> (the path itself or a link target on the way to <paramref name="fullPath"/>).</summary>
    private static string? VolumeRefusal(string fullPath, string path, Func<string, DriveType> driveTypeOf)
    {
        var via = string.Equals(path, fullPath, StringComparison.Ordinal) ? string.Empty : $" (through a link to '{path}')";
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)
            || root.StartsWith(@"\\", StringComparison.Ordinal) && !IsLocalDevicePath(root)
            || IsLocalDevicePath(root) && root.Contains(@"\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return $"Cannot move '{fullPath}' to the Recycle Bin{via}: a network location has none, and nothing was deleted.";
        }

        var drive = DriveRoot(root);
        if (drive is null)
        {
            return $"Cannot move '{fullPath}' to the Recycle Bin{via}: '{root}' is not a drive letter, and nothing was deleted.";
        }

        DriveType type;
        try
        {
            type = driveTypeOf(drive);
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return $"Cannot move '{fullPath}' to the Recycle Bin{via}: the type of its drive could not be read ({exception.Message}), and nothing was deleted.";
        }

        return type == DriveType.Fixed
            ? null
            : $"Cannot move '{fullPath}' to the Recycle Bin{via}: it is on a {type} drive, which has none, and nothing was deleted.";
    }

    /// <summary>
    /// The full targets of the links among <paramref name="path"/> and its existing ancestors, nearest first; a missing
    /// part of the path is skipped (it cannot be a link). The rest of the path after a link is kept under its target.
    /// </summary>
    private static IReadOnlyList<string> LinkTargets(string path)
    {
        var targets = new List<string>();
        var suffix = string.Empty;
        for (var current = Path.TrimEndingDirectorySeparator(path); !string.IsNullOrEmpty(current);)
        {
            var info = new DirectoryInfo(current);
            // Attributes is -1 for a part that does not exist; a dangling link still has its own attributes.
            if (info.Attributes != (FileAttributes)(-1) && info.LinkTarget is { } target)
            {
                var resolved = Path.GetFullPath(target, Path.GetDirectoryName(current) ?? current);
                targets.Add(suffix.Length == 0 ? resolved : Path.Join(resolved, suffix));
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                break;
            }

            var name = Path.GetFileName(current);
            suffix = suffix.Length == 0 ? name : Path.Join(name, suffix);
            current = parent;
        }

        return targets;
    }

    private static bool IsLocalDevicePath(string root) =>
        root.StartsWith(@"\\?\", StringComparison.Ordinal) || root.StartsWith(@"\\.\", StringComparison.Ordinal);

    /// <summary><c>C:\</c> from <c>C:\</c>, <c>\\?\C:\</c> or <c>\\.\C:\</c>; null for anything not named by a drive letter (a volume GUID).</summary>
    private static string? DriveRoot(string root)
    {
        var drive = IsLocalDevicePath(root) ? root[4..] : root;
        return drive.Length >= 2 && char.IsAsciiLetter(drive[0]) && drive[1] == ':'
            ? drive[0] + @":\"
            : null;
    }
}
