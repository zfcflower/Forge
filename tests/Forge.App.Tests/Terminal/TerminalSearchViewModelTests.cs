using System.Text;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Pins the buffer-search state on <see cref="TerminalViewModel"/>: matching across screen and scrollback, next/previous
/// navigation, the case toggle, the empty query, opening/closing (which clears highlights) and the scrollback anchor.
/// These tests drive the emulator directly, so they need no headless dispatcher.
/// </summary>
public sealed class TerminalSearchViewModelTests
{
    private static TerminalViewModel Open(string text)
    {
        var preferences = new AppPreferences();
        var viewModel = new TerminalViewModel(
            "s1",
            1,
            new FakeTerminalHost(),
            new TerminalStartRequest("s1", "C:\\work", new TerminalSize(100, 30)),
            preferences);
        lock (viewModel.Gate)
        {
            viewModel.Emulator.Feed(Encoding.UTF8.GetBytes(text));
        }

        return viewModel;
    }

    [Fact]
    public async Task Search_reports_matches_and_walks_them_with_next_and_previous()
    {
        await using var viewModel = Open("hello world hello");
        viewModel.SearchText = "hello";
        await viewModel.RunSearchAsync();

        Assert.Equal(2, viewModel.SearchMatchCount);
        Assert.NotNull(viewModel.CurrentSearchMatch);
        Assert.Equal("1/2", viewModel.SearchMatchLabel);

        viewModel.SearchNextCommand.Execute(null);
        Assert.Equal("2/2", viewModel.SearchMatchLabel);

        viewModel.SearchNextCommand.Execute(null);
        Assert.Equal("1/2", viewModel.SearchMatchLabel);

        viewModel.SearchPreviousCommand.Execute(null);
        Assert.Equal("2/2", viewModel.SearchMatchLabel);
    }

    [Fact]
    public async Task Search_honours_the_case_toggle()
    {
        await using var viewModel = Open("Hello hello");
        viewModel.SearchCaseSensitive = true;
        viewModel.SearchText = "hello";
        await viewModel.RunSearchAsync();

        Assert.Equal(1, viewModel.SearchMatchCount);

        viewModel.SearchCaseSensitive = false;
        await viewModel.RunSearchAsync();
        Assert.Equal(2, viewModel.SearchMatchCount);
    }

    [Fact]
    public async Task Empty_query_has_no_matches()
    {
        await using var viewModel = Open("hello");
        viewModel.SearchText = "hello";
        await viewModel.RunSearchAsync();
        Assert.Equal(1, viewModel.SearchMatchCount);

        viewModel.SearchText = string.Empty;
        Assert.Equal(0, viewModel.SearchMatchCount);
        Assert.Null(viewModel.CurrentSearchMatch);
    }

    [Fact]
    public async Task Close_clears_matches_and_hides_the_bar()
    {
        await using var viewModel = Open("hello");
        viewModel.OpenSearch();
        Assert.True(viewModel.IsSearchVisible);

        viewModel.SearchText = "hello";
        await viewModel.RunSearchAsync();
        Assert.Equal(1, viewModel.SearchMatchCount);

        viewModel.CloseSearch();
        Assert.False(viewModel.IsSearchVisible);
        Assert.Equal(0, viewModel.SearchMatchCount);
        Assert.Null(viewModel.CurrentSearchMatch);
    }

    [Fact]
    public async Task Search_finds_a_match_in_the_scrollback_and_its_anchor_survives_drops()
    {
        await using var viewModel = new TerminalViewModel(
            "s1",
            1,
            new FakeTerminalHost(),
            new TerminalStartRequest("s1", "C:\\work", new TerminalSize(100, 1)),
            new AppPreferences());
        lock (viewModel.Gate)
        {
            viewModel.Emulator.Feed(Encoding.UTF8.GetBytes("old\r\nnew"));
        }

        viewModel.SearchText = "old";
        await viewModel.RunSearchAsync();

        var match = viewModel.CurrentSearchMatch;
        Assert.NotNull(match);
        Assert.Equal(0, match.Value.AbsoluteLine(viewModel.Emulator.ScrollbackDropped));

        lock (viewModel.Gate)
        {
            viewModel.Emulator.ClearScrollback();
        }

        Assert.True(match.Value.AbsoluteLine(viewModel.Emulator.ScrollbackDropped) < 0, "the evicted line is no longer addressable");
    }

    [Fact]
    public async Task Open_raises_the_event_the_view_uses_to_focus_the_query_box()
    {
        await using var viewModel = Open("hello");
        var raised = 0;
        viewModel.SearchOpened += (_, _) => raised++;

        viewModel.OpenSearch();

        Assert.Equal(1, raised);
    }
}
