using Avalonia.Controls;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The startup notices (storage self-heal, dedupe of archived roots) arrive through
/// <c>IDialogService.NotifyAsync</c>, whose contract is "an information message with a single OK button". The shell used to
/// answer it with the confirm dialog and the same label for both buttons, so every notice showed two identical “确定”
/// buttons - a notice is not a question.
/// </summary>
[Collection("forge-app-ui")]
public sealed class NoticeDialogTests
{
    [Fact]
    public Task A_notice_dialog_has_exactly_one_acknowledge_button() => SharedUi.RunAsync(() =>
    {
        var content = ShellDialogs.NoticeContent("1 个会话的工作区目录已不存在，已标为已移除。", out var ok);

        var row = Assert.IsType<StackPanel>(content.Children[^1]);
        Assert.Same(ok, Assert.Single(row.Children));
        Assert.True(ok.IsDefault);

        // Esc must answer it too: with no cancel button, the acknowledge button is the dialog's cancel.
        Assert.True(ok.IsCancel);
        return Task.CompletedTask;
    });
}
