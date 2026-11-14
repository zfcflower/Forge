using Forge.App.Localization;

namespace Forge.App.ViewModels.Preview;

/// <summary>
/// The "no internal rendering" card: the file name, its size, a localized reason and the open-externally button.
/// Kinds: a Git LFS pointer (plus its parsed pointer fields), a binary file, a PDF and an image above the preview size
/// limit. An HTML page is not one of them: it opens as editable source and the toolbar's open button takes it to the
/// browser.
/// </summary>
internal sealed class PlaceholderFilePreviewViewModel : FilePreviewViewModel
{
    private readonly LocText reason;

    private PlaceholderFilePreviewViewModel(
        string path,
        string name,
        long byteSize,
        LocText reason,
        string captionKey,
        LfsPointer? lfs = null)
        : base(path, name, byteSize)
    {
        this.reason = reason;
        CaptionKey = captionKey;
        if (lfs is { } pointer)
        {
            LfsOid = pointer.Oid;
            LfsSize = pointer.Size;
            LfsVersion = pointer.Version;
        }

    }

    /// <summary>The localized reason, re-read on every access so a language switch refreshes it.</summary>
    public string Reason => reason.Resolve();

    public override string Caption => Loc.Instance[CaptionKey];

    private string CaptionKey { get; }

    /// <summary>The pointer's <c>oid</c> line (<c>sha256:…</c>), when the pointer had one.</summary>
    public string? LfsOid { get; }

    /// <summary>The pointer's <c>size</c> line, when the pointer had one.</summary>
    public long? LfsSize { get; }

    /// <summary>The pointer's <c>version</c> URL, when the pointer had one.</summary>
    public string? LfsVersion { get; }

    public bool HasLfsOid => LfsOid is not null;

    public bool HasLfsSize => LfsSize is not null;

    public bool HasLfsVersion => LfsVersion is not null;

    public string? LfsSizeText => LfsSize is { } size ? PreviewStrings.ByteSize(TextFormat.Number(size)) : null;

    public static PlaceholderFilePreviewViewModel ForLfs(string path, long byteSize, ReadOnlySpan<byte> firstBytes) =>
        new(path, System.IO.Path.GetFileName(path), byteSize, new LocText(PreviewStrings.LfsReasonKey), PreviewStrings.LfsCaptionKey, LfsPointerParser.Parse(firstBytes));

    public static PlaceholderFilePreviewViewModel ForBinary(string path, long byteSize) =>
        new(path, System.IO.Path.GetFileName(path), byteSize, new LocText(PreviewStrings.BinaryReasonKey), PreviewStrings.BinaryCaptionKey);

    public static PlaceholderFilePreviewViewModel ForPdf(string path, long byteSize) =>
        new(path, System.IO.Path.GetFileName(path), byteSize, new LocText(PreviewStrings.PdfReasonKey), PreviewStrings.PdfCaptionKey);

    public static PlaceholderFilePreviewViewModel ForImageTooLarge(string path, long byteSize, int limitBytes) =>
        new(path, System.IO.Path.GetFileName(path), byteSize, PreviewStrings.ImageTooLargeReason(limitBytes / FilePreviewSettings.BytesPerMiB), PreviewStrings.ImageTooLargeCaptionKey);
}
