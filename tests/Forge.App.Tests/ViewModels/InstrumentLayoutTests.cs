using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins the composer strip's width rule: it is a pure function of the width, so the priority ("only the git markers give way,
/// nothing else does") is asserted here instead of through a rendered strip.
/// </summary>
public sealed class InstrumentLayoutTests
{
    [Fact]
    public void The_git_markers_appear_exactly_at_their_threshold()
    {
        Assert.False(InstrumentLayout.For(InstrumentLayout.GitMinWidth - 1).ShowGit);
        Assert.True(InstrumentLayout.For(InstrumentLayout.GitMinWidth).ShowGit);
    }

    [Fact]
    public void A_narrower_strip_never_shows_more()
    {
        var previous = InstrumentLayout.For(0);
        for (var width = 1; width <= 1600; width++)
        {
            var current = InstrumentLayout.For(width);
            Assert.True(current.ShowGit || !previous.ShowGit, $"git came back at {width}");
            previous = current;
        }
    }

    [Fact]
    public void The_resources_button_the_sub_agent_toggle_the_workspace_button_and_the_readout_are_not_part_of_the_rule()
    {
        // They are always laid out; the only thing the width rule may drop is the git group.
        Assert.False(InstrumentLayout.For(0).ShowGit);
    }
}
