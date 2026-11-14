using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// The window side of the checks panel: a hand-off that has to wait in the composer keeps what the user already typed, and
/// a check's link is opened only when it is a web address (the link is whatever a CI integration reported).
/// </summary>
public sealed class ChecksHandOffTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void A_queued_hand_off_fills_an_empty_composer_with_the_message(string? draft)
    {
        Assert.Equal("fix the checks", MainWindowViewModel.ComposeQueuedDraft(draft, "fix the checks"));
    }

    [Fact]
    public void A_queued_hand_off_keeps_the_users_draft_and_appends_the_message()
    {
        var composed = MainWindowViewModel.ComposeQueuedDraft("half a thought\n\n", "fix the checks");

        Assert.Equal("half a thought\n\nfix the checks", composed);
    }

    [Theory]
    [InlineData("https://github.com/acme/forge/actions/runs/1/job/2", true)]
    [InlineData("http://ci.example.test/build/7", true)]
    [InlineData("HTTPS://example.test/", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("relative/path", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_web_links_are_opened(string? url, bool web)
    {
        Assert.Equal(web, MainWindowViewModel.IsWebLink(url));
    }

    [Fact]
    public void A_link_that_is_not_web_is_refused_without_starting_anything()
    {
        Assert.False(MainWindowViewModel.OpenChecksLink("file:///C:/Windows/System32/calc.exe"));
    }
}
