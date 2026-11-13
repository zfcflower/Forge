using System.Text;
using Forge.Abstractions;
using Forge.App.Localization;

namespace Forge.App.ViewModels;

// ToolRowViewModel is split by owner: this file is the row itself (call, status, result text, wave W8 adds the live output
// tail here); Transcript.ToolImages.cs holds the result image blocks and thumbnails (wave W4).
/// <summary>
/// One tool call: "▸ name · argument summary · ✓" collapsed, full arguments and result when expanded.
/// </summary>
internal sealed partial class ToolRowViewModel(string name, string argumentsJson) : TurnBlockViewModel
{
    private const int SummaryLimit = 160;

    private string status = "…";
    private LocText result;
    private bool hasResult;
    private bool isError;
    private bool isExpanded;

    public string Name { get; } = name;

    public string Arguments { get; } = argumentsJson;

    public string ArgumentSummary { get; } = Compact(argumentsJson, SummaryLimit);

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    /// <summary>The tool's answer in the current language; null until it returned something.</summary>
    public string? Result => hasResult ? result.Resolve() : null;

    public bool HasResult => hasResult;

    public bool IsError
    {
        get => isError;
        private set => Set(ref isError, value);
    }

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    public RelayCommand ToggleCommand => new(() => IsExpanded = !IsExpanded);

    public void Complete(ToolResult toolResult)
    {
        Complete(LocText.Literal(toolResult.Content), toolResult.IsError);
        CaptureImages(toolResult.Blocks);
    }

    /// <summary>
    /// Hook for the result's image blocks, implemented in <c>Transcript.ToolImages.cs</c> (wave W4). Without an implementation
    /// the call is compiled away.
    /// </summary>
    partial void CaptureImages(IReadOnlyList<ContentBlock> blocks);

    /// <summary>
    /// Closes the row with a result that is not the tool's own text - the placeholder of a call that never came back - so a
    /// language switch still reaches it.
    /// </summary>
    public void Complete(LocText content, bool isError)
    {
        result = content;
        hasResult = !string.IsNullOrEmpty(content.Resolve());
        IsError = isError;
        Status = isError ? "✗" : "✓";
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(HasResult));
    }

    public override void RefreshText()
    {
        OnPropertyChanged(nameof(Result));
        OnPropertyChanged(nameof(HasResult));
    }

    internal static string Compact(string text, int limit)
    {
        var builder = new StringBuilder(Math.Min(text.Length, limit));
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (builder.Length >= limit)
            {
                builder.Append('…');
                break;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
