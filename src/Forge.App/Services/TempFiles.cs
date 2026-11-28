using System.Security.Cryptography;
using System.Text;

namespace Forge.App.Services;

/// <summary>
/// The temp files FORGE hands to something that opens files: a preview tab, the OS application behind "open externally", a
/// picture the preview steps through. Everything inline in a message (a chat picture, an attachment, a stored report) has to
/// become a file first, so it lands under one folder of its own, named by a hash of its content — the same content always
/// reaches the same path, so a second click reuses the file instead of piling up copies — and each run sweeps what earlier
/// runs left behind.
/// </summary>
internal static class TempFiles
{
    /// <summary>The folders already swept by this run: one pass each, the first time anything is written to them.</summary>
    private static readonly HashSet<string> Swept = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder under the temp directory that one feature owns; nothing else writes there.</summary>
    internal static string Folder(string name) => Path.Combine(Path.GetTempPath(), name);

    /// <summary>
    /// The path for <paramref name="key"/> in <paramref name="folder"/>: the key's hash, then <paramref name="suffix"/> — the
    /// reader's own name for the file, extension included, since that is what a preview and the OS read. Written once.
    /// Callers clean <paramref name="suffix"/> to a bare file name first.
    /// </summary>
    internal static string PathFor(string folder, string key, string suffix)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return Path.Combine(folder, suffix.Length == 0 ? hash : hash + "-" + suffix);
    }

    /// <summary>Whether the file is already there, so a caller can skip decoding and writing the same bytes again.</summary>
    internal static bool Exists(string path) => File.Exists(path);

    /// <summary>
    /// Once per run and folder: removes what earlier runs left that is over a week old, so the temp directory does not grow
    /// with every click. A folder that cannot be listed or a file that cannot be deleted is the OS's business, never the
    /// caller's — the write that follows matters more.
    /// </summary>
    internal static void Sweep(string folder)
    {
        lock (Swept)
        {
            if (!Swept.Add(folder))
            {
                return;
            }
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7))
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
