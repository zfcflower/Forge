namespace Forge.App.ViewModels;

/// <summary>A transcript item whose text the copy button under it puts on the clipboard.</summary>
internal interface ICopyableText
{
    /// <summary>The whole message as plain text, read when the button is pressed.</summary>
    string CopyText { get; }
}
