using System.Text;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Terminal;
using Xunit;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// An empty result set must still reach the control (W16b review): the previous highlights stay on screen until something
/// else redraws it otherwise. The VM fires <see cref="TerminalViewModel.SearchMatchChanged"/> unconditionally, and the
/// control invalidates even when the current match is null.
/// </summary>
public sealed class TerminalSearchEmptyRedrawTests
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
    public async Task A_query_that_stops_matching_fires_the_match_change_so_the_highlights_clear()
    {
        var viewModel = Open("hello world\r\n");
        viewModel.SearchText = "hello";
        await viewModel.RunSearchAsync();
        Assert.True(viewModel.SearchMatchCount > 0);

        var fired = 0;
        viewModel.SearchMatchChanged += (_, _) => fired++;

        viewModel.SearchText = "hello2";
        await viewModel.RunSearchAsync();

        Assert.Equal(0, viewModel.SearchMatchCount);
        Assert.Null(viewModel.CurrentSearchMatch);
        Assert.True(fired > 0, "an empty result set must notify the control to redraw and drop the old highlights");
    }

    [Fact]
    public async Task Clearing_the_query_fires_the_match_change_too()
    {
        var viewModel = Open("hello world\r\n");
        viewModel.SearchText = "hello";
        await viewModel.RunSearchAsync();
        Assert.True(viewModel.SearchMatchCount > 0);

        var fired = 0;
        viewModel.SearchMatchChanged += (_, _) => fired++;

        viewModel.SearchText = string.Empty;
        await viewModel.RunSearchAsync();

        Assert.Equal(0, viewModel.SearchMatchCount);
        Assert.True(fired > 0, "clearing the box must clear the highlights immediately");
    }
}