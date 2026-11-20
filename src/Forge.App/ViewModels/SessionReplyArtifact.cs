using Forge.Abstractions;
using Forge.App.Services;

namespace Forge.App.ViewModels;

/// <summary>
/// The shell's "open the full report behind a receipt" entry (forge-core "reply receipt"): a report past the receipt's caps
/// (4000 characters / 250 lines) is stored as an artifact and the receipt carries its id, so the card offers that pointer back
/// instead of leaving the user with a number. The card is a leaf of the transcript, so the shell installs the opener here the
/// same way it installs <see cref="EditorFileOpener.Handler"/>.
/// </summary>
internal static class ReceiptReportOpener
{
    /// <summary>Opens one artifact by id; null in a host without a shell (a transcript rebuilt on its own draws no entry).</summary>
    internal static Action<string>? Handler { get; set; }
}

/// <summary>
/// The stored artifact as the file the preview tab opens: artifacts live in the artifact store, the preview opens
/// <em>files</em>, so the bytes are written under the temp folder first - the route the transcript's pictures already take
/// (<see cref="ChatPicture.Materialize"/>) - and old copies are swept away like the picture folder.
/// </summary>
internal static class ReceiptReport
{
    /// <summary>Where the copies go; nothing else lives here.</summary>
    internal static string Folder { get; } = Path.Combine(Path.GetTempPath(), "forge-reports");

    /// <summary>
    /// Writes one artifact's bytes as a markdown file and returns its path, or null when there is nothing to write. The name is
    /// the artifact id's hash, so two receipts never share a file and no stored name can write outside the folder. Runs on a
    /// pool thread.
    /// </summary>
    internal static string? Materialize(ArtifactInfo info, ReadOnlyMemory<byte> content)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (content.IsEmpty)
        {
            return null;
        }

        var folder = Folder;
        Directory.CreateDirectory(folder);
        PictureSet.Sweep(folder);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(info.Id)))[..16];
        var path = Path.Combine(folder, "report-" + hash.ToLowerInvariant() + ".md");
        File.WriteAllBytes(path, content.ToArray());
        return path;
    }
}
