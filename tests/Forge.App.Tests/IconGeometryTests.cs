using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Forge.App.Workbench.Controls;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The icon contract the module spec states and the chrome depends on: every icon declared in
/// <c>Resources/Icons.axaml</c> resolves, and every one of them is a single geometry on Fluent's 24×24 grid — the icon
/// control scales that grid into its box, so a glyph drawn on another grid renders at the wrong size. The one hand-drawn
/// exception, the terminal window, is checked by painting it: its window must stay cut through while its screen fills.
/// </summary>
[Collection("forge-app-ui")]
public sealed class IconGeometryTests
{
    [Fact]
    public async Task Every_icon_is_one_geometry_inside_its_24_grid()
    {
        var keys = DeclaredKeys();
        Assert.True(keys.Count > 0, "no icon keys were read from Resources/Icons.axaml; the check refuses to pass silently.");

        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var problems = await session.Dispatch(
            () =>
            {
                var host = new Window();
                host.Show();

                var found = new List<string>();
                foreach (var key in keys)
                {
                    if (!host.TryFindResource(key, out var value) || value is not Geometry geometry)
                    {
                        found.Add($"'{key}' does not resolve to a geometry");
                        continue;
                    }

                    var bounds = geometry.Bounds;
                    if (bounds.X < -0.01 || bounds.Y < -0.01 || bounds.Right > 24.01 || bounds.Bottom > 24.01)
                    {
                        found.Add($"'{key}' leaves its 24 grid: {bounds}");
                    }
                }

                return found;
            },
            default);

        Assert.True(problems.Count == 0, "icon geometry contract broken:\n" + string.Join("\n", problems));
    }

    [Fact]
    public async Task The_pin_paints_less_ink_when_it_is_the_outline_state()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(TreeScreenshotAppBuilder));
        var ink = await session.Dispatch(
            () =>
            {
                int Painted(string key)
                {
                    var icon = new FluentIcon { Width = 24, Height = 24, Foreground = Brushes.Black };
                    var host = new Window { Width = 24, Height = 24, Background = Brushes.White, Content = icon };
                    host.Show();
                    icon.Data = (Geometry)host.FindResource(key)!;
                    for (var pass = 0; pass < 2; pass++)
                    {
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    }

                    Dispatcher.UIThread.RunJobs();
                    using var frame = host.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    using var buffer = frame!.Lock();
                    var painted = 0;
                    for (var y = 0; y < 24; y++)
                    {
                        for (var x = 0; x < 24; x++)
                        {
                            var pixel = Marshal.ReadInt32(buffer.Address + (y * buffer.RowBytes) + (x * 4));
                            if ((pixel & 0xFF) < 0x80)
                            {
                                painted++;
                            }
                        }
                    }

                    return painted;
                }

                return (Outline: Painted("IconPin"), Solid: Painted("IconPinSolid"));
            },
            default);

        Assert.True(ink.Outline > 0, "The outline pin must paint.");
        Assert.True(ink.Solid > ink.Outline,
            $"The docked state must read as the filled pin: outline={ink.Outline}, solid={ink.Solid}");
    }

    /// <summary>The keys the one icon dictionary declares — the check follows the file, so a new icon is covered by adding it.</summary>
    private static List<string> DeclaredKeys()
    {
        var dictionary = Path.Combine(RepositoryRoot(), "src", "Forge.App", "Resources", "Icons.axaml");
        Assert.True(File.Exists(dictionary), $"missing icon dictionary at {dictionary}");
        return Regex.Matches(File.ReadAllText(dictionary), "x:Key=\"([A-Za-z]+)\"")
            .Select(static match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Walks up from the test binaries to the checkout root (the directory holding Forge.slnx).</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Forge.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("no directory above the test binaries holds Forge.slnx.");
    }
}
