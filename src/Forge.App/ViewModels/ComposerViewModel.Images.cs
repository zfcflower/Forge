using System.Collections.ObjectModel;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Notifications;
using Forge.Core.Providers;
using Forge.Core.Storage;

namespace Forge.App.ViewModels;

/// <summary>
/// The composer's attachments: images (thumbnails, <see cref="Attachments"/>) and files - PDF, audio, video, text and
/// source files (chips, <see cref="Files"/>) - pasted, dropped or picked, waiting until the message is sent, queued or
/// interjected, which takes them all (<see cref="TakeAttachments"/>). Every way in goes through one classifier
/// (<see cref="FileAttachmentRules"/>) and one gate: a kind the current model is known not to take, or a file the current
/// provider's wire format cannot carry, is refused with a warning toast; an unknown capability allows it. When the model
/// changes under attachments it no longer takes, they stay, sending is blocked and the composer says why, until the user
/// removes them. Reading, decoding and encoding run off the UI thread; the lists are only touched on the UI thread.
/// </summary>
internal sealed partial class ComposerViewModel
{
    private readonly IImageCodec imageCodec;
    private readonly Func<string?, string?, ModelInputs?> imageInputs;
    private readonly Func<string?, string, bool> wireCarries;
    private readonly INotifications? notifications;
    private readonly string notificationScope;
    private bool acceptsImages = true;
    private string? attachmentError;
    private ModelInputs blockedKinds;

    /// <summary>Images being decoded right now; they count against the per-message limit before they land.</summary>
    private int preparing;

    /// <summary>Files being read right now; they count against the per-message limit before they land.</summary>
    private int preparingFiles;

    /// <summary>Raised whenever the pending attachments or their sendability change, so the session re-evaluates sending.</summary>
    public event Action? AttachmentsChanged;

    /// <summary>The pending images, as thumbnails.</summary>
    public ObservableCollection<PendingImageViewModel> Attachments { get; }

    /// <summary>The pending non-image files, as chips.</summary>
    public ObservableCollection<PendingFileViewModel> Files { get; }

    public bool HasAttachments => Attachments.Count > 0 || Files.Count > 0;

    public bool HasImageAttachments => Attachments.Count > 0;

    public bool HasFileAttachments => Files.Count > 0;

    /// <summary>
    /// False when the current model's catalogue entry lists its inputs and images are not among them. An unknown entry
    /// allows images; the provider's own error is the fallback.
    /// </summary>
    public bool AcceptsImages => acceptsImages;

    /// <summary>The hint under the input while the current model rejects images; empty otherwise.</summary>
    public string ImageGateHint => acceptsImages ? string.Empty : ImageStrings.ModelRejects;

    /// <summary>
    /// True while something attached is of a kind the current model is known not to take, or that its provider cannot carry
    /// (the model was switched after attaching): the message cannot be sent until those attachments are removed.
    /// </summary>
    public bool AttachmentsBlocked => blockedKinds != ModelInputs.None;

    /// <summary>Why sending is blocked, naming the refused kinds; empty while nothing is blocked.</summary>
    public string AttachmentsBlockedHint =>
        blockedKinds == ModelInputs.None ? string.Empty : AttachmentStrings.Blocked(blockedKinds).Resolve();

    /// <summary>Why the last attachment was not added; null when there is nothing to say.</summary>
    public string? AttachmentError
    {
        get => attachmentError;
        private set
        {
            if (Set(ref attachmentError, value))
            {
                OnPropertyChanged(nameof(HasAttachmentError));
            }
        }
    }

    public bool HasAttachmentError => !string.IsNullOrEmpty(attachmentError);

    public void DismissAttachmentError() => AttachmentError = null;

    /// <summary>
    /// Attaches an encoded image (a dropped or picked file's bytes, or a pasted bitmap encoded as PNG). The format is read
    /// from the bytes, not from the name. Returns why it was refused, or <see cref="AttachmentRejection.None"/>; a refusal
    /// also sets <see cref="AttachmentError"/>.
    /// </summary>
    public async Task<AttachmentRejection> AddImageAsync(byte[] data, string displayName)
    {
        ArgumentNullException.ThrowIfNull(data);
        displayName = string.IsNullOrWhiteSpace(displayName) ? ImageStrings.ClipboardName : displayName;
        if (!acceptsImages)
        {
            return RefuseImageByModel();
        }

        if (Attachments.Count + preparing >= ImageAttachmentRules.MaxImagesPerMessage)
        {
            return Reject(AttachmentRejection.TooMany, ImageStrings.TooMany);
        }

        var mediaType = ImageAttachmentRules.SniffMediaType(data);
        if (mediaType is null)
        {
            return Reject(AttachmentRejection.Unsupported, ImageStrings.Unsupported(displayName));
        }

        if (!WireCarries(mediaType))
        {
            return RefuseWithToast(
                AttachmentRejection.WireUnsupported,
                AttachmentStrings.WireUnsupported(displayName, AttachmentKind.Image, mediaType));
        }

        preparing++;
        PreparedImage? prepared;
        string? base64 = null;
        try
        {
            prepared = await Task.Run(() =>
            {
                try
                {
                    var image = imageCodec.Prepare(data, mediaType);
                    if (image.Bytes.Length <= ImageAttachmentRules.MaxEncodedBytes)
                    {
                        base64 = Convert.ToBase64String(image.Bytes);
                    }

                    return image;
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            }).ConfigureAwait(true);
        }
        finally
        {
            preparing--;
        }

        if (prepared is null)
        {
            return Reject(AttachmentRejection.Unsupported, ImageStrings.Unsupported(displayName));
        }

        if (base64 is null)
        {
            prepared.Thumbnail?.Dispose();
            return Reject(AttachmentRejection.TooLarge, ImageStrings.TooLarge(displayName));
        }

        Attachments.Add(new PendingImageViewModel(
            new ImageBlock(prepared.MediaType, base64),
            prepared.Bytes.Length,
            prepared.Thumbnail,
            RemoveAttachment));
        AttachmentError = null;
        OnAttachmentsChanged();
        return AttachmentRejection.None;
    }

    /// <summary>Reads an image file off the UI thread and attaches it; a read failure is reported like a refusal.</summary>
    public async Task<AttachmentRejection> AddImageFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var name = Path.GetFileName(path);
        if (!acceptsImages)
        {
            return RefuseImageByModel();
        }

        if (!ImageAttachmentRules.HasImageExtension(path))
        {
            // Not read at all: a dropped archive or document is refused by its name before any bytes are loaded.
            return Reject(AttachmentRejection.Unsupported, ImageStrings.Unsupported(name));
        }

        byte[] data;
        try
        {
            // A file far over the limit cannot shrink below it by scaling in every case, but a large photo can, so the read
            // itself is only bounded loosely to keep a stray multi-gigabyte drop from being loaded.
            var info = new FileInfo(path);
            if (info.Exists && info.Length > FileAttachmentRules.MaxBytes(AttachmentKind.Image))
            {
                return Reject(AttachmentRejection.TooLarge, ImageStrings.TooLarge(name));
            }

            data = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Reject(AttachmentRejection.Unsupported, ImageStrings.ReadFailed(name, exception.Message));
        }

        return await AddImageAsync(data, name).ConfigureAwait(true);
    }

    /// <summary>
    /// Attaches any file the composer understands: an image goes the image way (scaled, thumbnailed), a PDF, audio or video
    /// file becomes a <see cref="FileBlock"/>, a text or source file is inlined. The file is bounded by its claimed kind's
    /// limit before it is read, then classified by its bytes. Returns why it was refused, or
    /// <see cref="AttachmentRejection.None"/>.
    /// </summary>
    public async Task<AttachmentRejection> AddFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var name = Path.GetFileName(path);
        var claimed = FileAttachmentRules.KindFromExtension(path);
        if (claimed == AttachmentKind.Image)
        {
            return await AddImageFileAsync(path).ConfigureAwait(true);
        }

        // Refused by name before any bytes are read: the kind is known and this model cannot take it.
        if (claimed is { } kind && kind != AttachmentKind.Text && GateByModel(kind, name) is { } early)
        {
            return early;
        }

        if (Files.Count + preparingFiles >= FileAttachmentRules.MaxFilesPerMessage)
        {
            return Reject(AttachmentRejection.TooMany, AttachmentStrings.TooMany);
        }

        byte[] data;
        preparingFiles++;
        try
        {
            var limit = FileAttachmentRules.ReadLimit(path);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > limit)
            {
                return Reject(AttachmentRejection.TooLarge, AttachmentStrings.TooLarge(name, limit).Resolve());
            }

            data = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Reject(AttachmentRejection.Unsupported, ImageStrings.ReadFailed(name, exception.Message));
        }
        finally
        {
            preparingFiles--;
        }

        return await AddFileAsync(data, name, claimed).ConfigureAwait(true);
    }

    /// <summary>Attaches a file's bytes (see <see cref="AddFileAsync(string)"/>); <paramref name="name"/> is shown and sent.</summary>
    public Task<AttachmentRejection> AddFileAsync(byte[] data, string name) =>
        AddFileAsync(data, name, FileAttachmentRules.KindFromExtension(name));

    /// <summary>Removes and returns every pending attachment - images first, then files, each in attaching order.</summary>
    public IReadOnlyList<ContentBlock> TakeAttachments()
    {
        if (!HasAttachments)
        {
            return [];
        }

        var images = Attachments.ToArray();
        var files = Files.ToArray();
        var taken = new ContentBlock[images.Length + files.Length];
        for (var i = 0; i < images.Length; i++)
        {
            taken[i] = images[i].Block;
        }

        for (var i = 0; i < files.Length; i++)
        {
            taken[images.Length + i] = files[i].Block;
        }

        Attachments.Clear();
        Files.Clear();
        foreach (var image in images)
        {
            image.LoadedThumbnail?.Dispose();
        }

        AttachmentError = null;
        OnAttachmentsChanged();
        return taken;
    }

    /// <summary>
    /// Puts attachments back (a message the session refused to start, or the composer state carried to a new view). Images
    /// are re-added without a thumbnail decode on the UI thread (previews load lazily like the transcript's); files come back
    /// as chips. They are not re-gated: if the current model cannot take them, sending is blocked until they are removed.
    /// </summary>
    public void RestoreAttachments(IReadOnlyList<ContentBlock> attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count == 0)
        {
            return;
        }

        foreach (var block in attachments)
        {
            if (block is ImageBlock image)
            {
                if (Attachments.Count < ImageAttachmentRules.MaxImagesPerMessage)
                {
                    Attachments.Add(new PendingImageViewModel(
                        image,
                        MessageJson.DecodedLength(image.Data),
                        thumbnail: null,
                        RemoveAttachment));
                }

                continue;
            }

            if (Files.Count < FileAttachmentRules.MaxFilesPerMessage && FileChipViewModel.Describe(block) is { } file)
            {
                Files.Add(new PendingFileViewModel(block, file.Name, file.Kind, file.MediaType, file.Bytes, RemoveFile));
            }
        }

        OnAttachmentsChanged();
    }

    private async Task<AttachmentRejection> AddFileAsync(byte[] data, string name, AttachmentKind? claimed)
    {
        ArgumentNullException.ThrowIfNull(data);
        name = string.IsNullOrWhiteSpace(name) ? ImageStrings.ClipboardName : name;
        var type = FileAttachmentRules.Classify(data, name);
        if (type is null)
        {
            return claimed == AttachmentKind.Text
                ? RefuseWithToast(AttachmentRejection.NotText, AttachmentStrings.NotText(name))
                : Reject(AttachmentRejection.Unsupported, AttachmentStrings.Unsupported(name).Resolve());
        }

        var (kind, mediaType) = type.Value;
        if (kind == AttachmentKind.Image)
        {
            return await AddImageAsync(data, name).ConfigureAwait(true);
        }

        if (kind != AttachmentKind.Text)
        {
            if (GateByModel(kind, name) is { } refused)
            {
                return refused;
            }

            if (!WireCarries(mediaType))
            {
                return RefuseWithToast(
                    AttachmentRejection.WireUnsupported,
                    AttachmentStrings.WireUnsupported(name, kind, mediaType));
            }
        }

        var limit = FileAttachmentRules.MaxBytes(kind);
        if (data.LongLength > limit)
        {
            return Reject(AttachmentRejection.TooLarge, AttachmentStrings.TooLarge(name, limit).Resolve());
        }

        if (Files.Count + preparingFiles >= FileAttachmentRules.MaxFilesPerMessage)
        {
            return Reject(AttachmentRejection.TooMany, AttachmentStrings.TooMany);
        }

        preparingFiles++;
        ContentBlock? block;
        try
        {
            block = await Task.Run(() => Encode(data, name, kind, mediaType)).ConfigureAwait(true);
        }
        finally
        {
            preparingFiles--;
        }

        if (block is null)
        {
            return RefuseWithToast(AttachmentRejection.NotText, AttachmentStrings.NotText(name));
        }

        Files.Add(new PendingFileViewModel(block, name, kind, mediaType, data.LongLength, RemoveFile));
        AttachmentError = null;
        OnAttachmentsChanged();
        return AttachmentRejection.None;
    }

    /// <summary>The block a classified file is sent as; null for a text file that does not decode as UTF-8.</summary>
    private static ContentBlock? Encode(byte[] data, string name, AttachmentKind kind, string mediaType)
    {
        if (kind != AttachmentKind.Text)
        {
            return new FileBlock(mediaType, Convert.ToBase64String(data), name);
        }

        return FileAttachmentRules.TryDecodeText(data, out var text) ? TextAttachment.Create(name, text) : null;
    }

    /// <summary>Refuses <paramref name="kind"/> when the current model's inputs are known and lack it; null when allowed.</summary>
    private AttachmentRejection? GateByModel(AttachmentKind kind, string name)
    {
        if (kind == AttachmentKind.Image)
        {
            return acceptsImages ? null : RefuseImageByModel();
        }

        return CurrentInputs() is { } known && (known & FileAttachmentRules.Flag(kind)) == 0
            ? RefuseWithToast(AttachmentRejection.ModelRejects, AttachmentStrings.ModelRejects(name, kind))
            : null;
    }

    private AttachmentRejection RefuseImageByModel() =>
        RefuseWithToast(AttachmentRejection.ModelRejects, new LocText(ImageStrings.ModelRejectsKey));

    private void RemoveAttachment(PendingImageViewModel image)
    {
        if (Attachments.Remove(image))
        {
            image.LoadedThumbnail?.Dispose();
            AttachmentError = null;
            OnAttachmentsChanged();
        }
    }

    private void RemoveFile(PendingFileViewModel file)
    {
        if (Files.Remove(file))
        {
            AttachmentError = null;
            OnAttachmentsChanged();
        }
    }

    private AttachmentRejection Reject(AttachmentRejection reason, string message)
    {
        AttachmentError = message;
        return reason;
    }

    /// <summary>
    /// A refusal the user may not have caused on purpose (the model or provider cannot take it, the file is not text): it is
    /// shown under the input and raised as a warning toast. The key is stable per session and reason, so repeated drops of
    /// refused files count up on one toast instead of stacking.
    /// </summary>
    private AttachmentRejection RefuseWithToast(AttachmentRejection reason, LocText message)
    {
        AttachmentError = message.Resolve();
        notifications.Notify(
            NotificationLevel.Warning,
            AttachmentStrings.RefusedTitle,
            message,
            "attachment-refused:" + notificationScope + ":" + reason);
        return reason;
    }

    private void OnAttachmentsChanged()
    {
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(HasImageAttachments));
        OnPropertyChanged(nameof(HasFileAttachments));
        RefreshAttachmentGate();
        AttachmentsChanged?.Invoke();
    }

    private ModelInputs? CurrentInputs()
    {
        try
        {
            return imageInputs(model, providerId);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private bool WireCarries(string mediaType)
    {
        try
        {
            return wireCarries(providerId, mediaType);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Unknown: let the provider's own error be the fallback, as for an unknown model.
            return true;
        }
    }

    /// <summary>The default wire check: the connected provider's protocol; an unknown provider carries everything.</summary>
    private bool DefaultWireCarries(string? provider, string mediaType) =>
        catalog.Find(provider) is not { } connected || ProviderAttachments.CanCarry(connected.Protocol, mediaType);

    /// <summary>Re-reads whether the current model takes images; called whenever the model or the catalogue changes.</summary>
    private void RefreshImageGate()
    {
        var inputs = CurrentInputs();
        var accepts = inputs is not { } known || (known & ModelInputs.Image) != 0;
        if (Set(ref acceptsImages, accepts, nameof(AcceptsImages)))
        {
            OnPropertyChanged(nameof(ImageGateHint));
        }

        if (RefreshAttachmentGate())
        {
            AttachmentsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Re-evaluates the pending attachments against the current model and provider. Entering the blocked state raises one
    /// warning toast (stable key per session); leaving it is silent. Returns whether the state changed.
    /// </summary>
    private bool RefreshAttachmentGate()
    {
        var blocked = ModelInputs.None;
        if (HasAttachments)
        {
            var inputs = CurrentInputs();
            foreach (var image in Attachments)
            {
                if (!Allowed(AttachmentKind.Image, image.Block.MediaType, inputs))
                {
                    blocked |= ModelInputs.Image;
                }
            }

            foreach (var file in Files)
            {
                if (file.Kind != AttachmentKind.Text && !Allowed(file.Kind, file.MediaType, inputs))
                {
                    blocked |= FileAttachmentRules.Flag(file.Kind);
                }
            }
        }

        if (blocked == blockedKinds)
        {
            return false;
        }

        var wasBlocked = blockedKinds != ModelInputs.None;
        blockedKinds = blocked;
        OnPropertyChanged(nameof(AttachmentsBlocked));
        OnPropertyChanged(nameof(AttachmentsBlockedHint));
        if (!wasBlocked && blocked != ModelInputs.None)
        {
            notifications.Notify(
                NotificationLevel.Warning,
                AttachmentStrings.BlockedTitle,
                AttachmentStrings.Blocked(blocked),
                "attachments-blocked:" + notificationScope);
        }

        return true;
    }

    private bool Allowed(AttachmentKind kind, string mediaType, ModelInputs? inputs) =>
        (inputs is not { } known || (known & FileAttachmentRules.Flag(kind)) != 0) && WireCarries(mediaType);

    private void RefreshImageText()
    {
        OnPropertyChanged(nameof(ImageGateHint));
        OnPropertyChanged(nameof(AttachmentsBlockedHint));
        foreach (var attachment in Attachments)
        {
            attachment.RefreshText();
        }

        foreach (var file in Files)
        {
            file.RefreshText();
        }
    }
}
