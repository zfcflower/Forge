using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The selection standard of the module SPEC's 「交互色」: one translucent brand tint behind selected text, no selection
/// foreground (selected ink keeps its own colour), and one brush behind all three surfaces. The composer matters most
/// here: its TextBox selection comes from Fluent's own key, so the test pins that <c>Tokens.axaml</c> points
/// <c>TextControlSelectionHighlightColor</c> at the same brush a transcript card wears.
/// </summary>
[Collection("forge-app-ui")]
public sealed class TextSelectionColorTests
{
    [Fact]
    public Task The_transcript_the_input_and_the_editor_share_one_translucent_selection() => SharedUi.RunAsync(() =>
    {
        var card = new SelectableTextBlock { Text = "abc" };
        var input = new TextBox { Text = "abc", Classes = { "composer-input" } };
        var window = new Window
        {
            Width = 640,
            Height = 480,
            Content = new StackPanel { Children = { card, input } },
        };
        window.Show();
        SharedUi.Settle(window);

        // Both themes define the standard, and every surface reads that one brush: the aliases must be the selection
        // token itself, not a copied colour a later tweak would leave behind.
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var selection = Assert.IsAssignableFrom<ISolidColorBrush>(Resource(window, "SelectionBackgroundBrush", variant));
            Assert.True(selection.Color.A is > 0 and < 255, $"the selection background is translucent ({variant})");
            Assert.Same(selection, Resource(window, "TextControlSelectionHighlightColor", variant));
            Assert.Same(selection, Resource(window, "EditorSelectionBrush", variant));
        }

        // The attached controls read the current theme's brush; a foreground override would recolour selected text.
        var current = Assert.IsAssignableFrom<ISolidColorBrush>(Resource(window, "SelectionBackgroundBrush", window.ActualThemeVariant));
        Assert.Same(current, card.SelectionBrush);
        Assert.Null(card.SelectionForegroundBrush);
        Assert.Same(current, input.SelectionBrush);
        Assert.Null(input.SelectionForegroundBrush);

        window.Close();
        return Task.CompletedTask;
    });

    private static IBrush Resource(Visual anchor, string key, ThemeVariant variant)
    {
        Assert.True(anchor.TryFindResource(key, variant, out var value), "missing resource: " + key + " (" + variant + ")");
        return Assert.IsAssignableFrom<IBrush>(value);
    }
}
