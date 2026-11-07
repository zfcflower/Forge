using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Forge.App.ViewModels;

namespace Forge.App.Views;

public partial class FileDocumentView : UserControl
{
    public FileDocumentView()
    {
        InitializeComponent();
        LineList.KeyDown += OnListKeyDown;
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            e.Handled = true;
            _ = CopyAsync();
        }
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e) => _ = CopyAsync();

    private async Task CopyAsync()
    {
        if (DataContext is not FileDocumentViewModel model)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            model.ReportCopyError("clipboard unavailable");
            return;
        }

        var text = SelectedText(model);
        try
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
            model.ReportCopyError(string.Empty);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            System.Diagnostics.Trace.WriteLine("[file] copy failed: " + exception.Message);
            model.ReportCopyError(exception.Message);
        }
    }

    private string SelectedText(FileDocumentViewModel model)
    {
        if (model.IsEditing)
        {
            return string.IsNullOrEmpty(Editor.SelectedText) ? model.EditText : Editor.SelectedText;
        }

        var first = int.MaxValue;
        var last = 0;
        foreach (var item in LineList.SelectedItems ?? Array.Empty<object>())
        {
            if (item is FileLineViewModel line)
            {
                first = Math.Min(first, line.Number);
                last = Math.Max(last, line.Number);
            }
        }

        return last == 0 ? model.EditText : model.GetLinesText(first, last);
    }
}
