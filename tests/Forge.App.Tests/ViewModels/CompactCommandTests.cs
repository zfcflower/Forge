using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>The composer's <c>/compact [focus]</c> command: the word in any case, optionally followed by focus text.</summary>
public sealed class CompactCommandTests
{
    [Theory]
    [InlineData("/compact", null)]
    [InlineData("  /compact  ", null)]
    [InlineData("/COMPACT", null)]
    [InlineData("/Compact keep the API notes", "keep the API notes")]
    [InlineData("/COMPACT  focus text  ", "focus text")]
    [InlineData("/compact\n  multi\nline  ", "multi\nline")]
    public void The_command_is_recognised_with_its_trimmed_focus(string text, string? focus)
    {
        Assert.True(SessionWorkspaceViewModel.TryParseCompactCommand(text, out var parsed));
        Assert.Equal(focus, parsed);
        Assert.True(SessionWorkspaceViewModel.IsCompactCommand(text));
    }

    [Theory]
    [InlineData("/compactfoo")]
    [InlineData("/compact-now")]
    [InlineData("please /compact")]
    [InlineData("/compac")]
    [InlineData("")]
    public void Other_text_is_not_the_command(string text)
    {
        Assert.False(SessionWorkspaceViewModel.TryParseCompactCommand(text, out var parsed));
        Assert.Null(parsed);
        Assert.False(SessionWorkspaceViewModel.IsCompactCommand(text));
    }
}
