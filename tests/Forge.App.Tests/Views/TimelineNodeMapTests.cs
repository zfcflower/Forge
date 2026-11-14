using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests.Views;

/// <summary>
/// The timeline node icon contract (forge-app spec "聊天时间轴(W13)"): for every node kind the outline and the filled
/// variant both resolve in the running application to a single geometry on Fluent's 24×24 grid — a missing key would show
/// as a blank node, a foreign grid would render at the wrong size. Mirrors <c>IconGeometryTests</c> but follows the
/// mapping table instead of the dictionary file, so only the icons the timeline actually wears are covered.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TimelineNodeMapTests
{
    [Fact]
    public async Task Every_node_kinds_outline_and_filled_icon_resolves_on_the_24_grid()
    {
        var problems = await SharedUi.Session.Dispatch(
            () =>
            {
                var host = new Window();
                host.Show();
                try
                {
                    var found = new List<string>();
                    foreach (TimelineNodeKind kind in Enum.GetValues<TimelineNodeKind>())
                    {
                        foreach (var filled in (bool[])[false, true])
                        {
                            var key = TimelineNodeMap.IconKey(kind, filled);
                            if (!host.TryFindResource(key, out var value) || value is not StreamGeometry geometry)
                            {
                                found.Add($"'{key}' ({kind}, filled={filled}) does not resolve to a StreamGeometry");
                                continue;
                            }

                            var bounds = geometry.Bounds;
                            if (bounds.X < -0.01 || bounds.Y < -0.01 || bounds.Right > 24.01 || bounds.Bottom > 24.01)
                            {
                                found.Add($"'{key}' ({kind}, filled={filled}) leaves its 24 grid: {bounds}");
                            }
                        }
                    }

                    return found;
                }
                finally
                {
                    host.Close();
                }
            },
            default);

        Assert.True(
            problems.Count == 0,
            "timeline node icon contract broken:\n" + string.Join("\n", problems));
    }
}
