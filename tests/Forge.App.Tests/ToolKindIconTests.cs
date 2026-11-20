using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// One icon per tool *kind*, and the split that makes copying, moving and deleting readable: `ls` is a folder list, `copy`
/// a copy, `move` a tray-with-arrow, `delete` a bin — none of them the pencil that means "edit". The icons themselves are
/// hand-copied geometry, one of them scaled from the 20 px set, so the resource key is asserted to resolve to a path that
/// actually parses.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ToolKindIconTests
{
    [Fact]
    public void A_tool_name_maps_to_its_own_kind()
    {
        (string Name, TimelineNodeKind Kind)[] expected =
        [
            ("ls", TimelineNodeKind.ListDirectory),
            ("find", TimelineNodeKind.Search),
            ("grep", TimelineNodeKind.Search),
            ("edit", TimelineNodeKind.EditFile),
            ("write", TimelineNodeKind.EditFile),
            ("copy", TimelineNodeKind.CopyFile),
            ("move", TimelineNodeKind.MoveFile),
            ("delete", TimelineNodeKind.DeleteFile),
            ("bash", TimelineNodeKind.Command),
            ("no_such_tool", TimelineNodeKind.Tool),
        ];

        foreach (var (name, kind) in expected)
        {
            Assert.Equal(kind, TimelineNodeMap.KindForTool(name));
        }
    }

    /// <summary>
    /// The four split kinds resolve in both weights: a missing key would draw an empty node while a call runs or fails.
    /// </summary>
    [Fact]
    public Task Every_split_kind_resolves_to_a_parseable_icon() => SharedUi.RunAsync(() =>
    {
        TimelineNodeKind[] kinds =
        [
            TimelineNodeKind.ListDirectory,
            TimelineNodeKind.CopyFile,
            TimelineNodeKind.MoveFile,
            TimelineNodeKind.DeleteFile,
        ];

        bool[] weights = [false, true];
        foreach (var kind in kinds)
        {
            foreach (var filled in weights)
            {
                var key = TimelineNodeMap.IconKey(kind, filled);
                Assert.True(Application.Current!.TryFindResource(key, out var value), $"missing resource {key}");

                // Reading Bounds is what parses the hand-copied path: a malformed one throws here instead of drawing nothing.
                var bounds = Assert.IsType<StreamGeometry>(value).Bounds;
                Assert.True(bounds.Width is > 10 and <= 24.5, $"{key} width {bounds.Width}");
                Assert.True(bounds.Height is > 10 and <= 24.5, $"{key} height {bounds.Height}");
            }
        }

        // Two kinds must not share a glyph, or the split would be invisible.
        var icons = kinds.SelectMany(kind => weights.Select(filled => TimelineNodeMap.IconKey(kind, filled)));
        Assert.Equal(icons.Count(), icons.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(8, icons.Count());
        return Task.CompletedTask;
    });

    /// <summary>Each kind also names itself, so the node's screen-reader text is not a bare "Tool".</summary>
    [Fact]
    public void Every_split_kind_has_its_own_name()
    {
        TimelineNodeKind[] kinds =
        [
            TimelineNodeKind.ListDirectory,
            TimelineNodeKind.CopyFile,
            TimelineNodeKind.MoveFile,
            TimelineNodeKind.DeleteFile,
        ];

        var keys = kinds.Select(TimelineNodeMap.NameKey).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(TimelineStrings.NameTool, keys);
    }
}
