using Avalonia.Controls;
using Avalonia.Input.Platform;
using Forge.App.Logging;
using Forge.App.ViewModels;
using ZLogger;

namespace Forge.App.Views;

/// <summary>
/// The copy button under a message: on click it puts the <see cref="ICopyableText.CopyText"/> of its data context on the
/// clipboard. The text is read at the click, never bound, so a streaming reply does not rebuild it on every delta.
/// </summary>
internal sealed class CopyTextButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    protected override void OnClick()
    {
        base.OnClick();
        if (DataContext is ICopyableText { CopyText: { Length: > 0 } text } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            _ = CopyAsync(clipboard, text);
        }
    }

    private static async Task CopyAsync(Avalonia.Input.Platform.IClipboard clipboard, string text)
    {
        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLogs.Session.ZLogWarning($"copy failed: {exception.Message}");
        }
    }
}
