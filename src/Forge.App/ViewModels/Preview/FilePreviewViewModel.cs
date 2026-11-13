using System.ComponentModel;
using System.Diagnostics;
using Forge.App.Localization;

namespace Forge.App.ViewModels.Preview;

/// <summary>
/// One read-only preview of a file in the inspector. Every preview carries the path, display name and on-disk size of
/// the file, a localized caption, an "open with the system program" command and a localized notice line for failures
/// (decode, parse, open). Previews never create an editor document, so the document stays clean and unsavable.
/// </summary>
internal abstract class FilePreviewViewModel : ObservableObject
{
    private LocText notice;

    protected FilePreviewViewModel(string path, string name, long byteSize)
    {
        Path = path;
        Name = name;
        ByteSize = byteSize;
        OpenExternallyCommand = new RelayCommand(OpenExternally);
    }

    public string Path { get; }

    /// <summary>The file name, shown on the card; never translated.</summary>
    public string Name { get; }

    public long ByteSize { get; }

    /// <summary>The grouped byte size, for the cards.</summary>
    public string ByteSizeText => SizeText(ByteSize);

    /// <summary>The one-line description, re-read on every access so a language switch refreshes it.</summary>
    public abstract string Caption { get; }

    public RelayCommand OpenExternallyCommand { get; }

    /// <summary>The read/decode problem, in the language of the moment it is read; null when the preview is complete.</summary>
    public string? Notice => notice.IsEmpty ? null : notice.Resolve();

    public bool HasNotice => !notice.IsEmpty;

    /// <summary>Disposes decoded images; called exactly once when the preview leaves the screen.</summary>
    public virtual void Release()
    {
    }

    /// <summary>The file's size for captions and cards, grouped with the byte unit.</summary>
    protected static string SizeText(long bytes) => PreviewStrings.ByteSize(TextFormat.Number(bytes));

    protected void SetNotice(LocText value)
    {
        notice = value;
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(HasNotice));
    }

    private void OpenExternally()
    {
        if (PreviewOpener.TryOpen(Path) is { } failure)
        {
            SetNotice(failure);
        }
    }
}

/// <summary>Opens a file with the OS default program, the same call the main window's reveal uses.</summary>
internal static class PreviewOpener
{
    /// <summary>A localized notice when the shell refused, or null when the open request was accepted.</summary>
    public static LocText? TryOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return PreviewStrings.OpenFailed(System.IO.Path.GetFileName(path), exception.Message);
        }
    }
}
