using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Forge.App.Composition;
using Forge.App.Input;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Settings;
using Forge.App.ViewModels;
using Forge.App.Views;
using Xunit;

namespace Forge.App.Tests.Keymap;

/// <summary>
/// The two places the keymap reaches a window the way the app builds it, rather than the hand-built windows of
/// <see cref="KeymapHeadlessTests"/>: the real <see cref="MainWindow"/> over the composition root (its four key bindings come
/// from the applier, so a rebind has to rebuild them, and Ctrl+P has to open the file palette) and the section view's capture
/// surface (a click on a row's rebind button focuses it, a key press becomes the captured gesture, and confirming writes the
/// override). Without these the wave is only proven against stand-ins.
/// </summary>
[Collection("forge-app-ui")]
public sealed class KeymapShellTests
{
    [Fact]
    public Task The_real_main_window_binds_the_four_window_gestures_and_ctrl_p_opens_the_file_palette() => SharedUi.RunAsync(async () =>
    {
        var (root, previous) = TempDataRoot.Redirect("forge-keymap-shell-");
        AppComposition? composition = null;
        MainWindow? window = null;
        try
        {
            composition = AppComposition.Create();
            var model = new MainWindowViewModel(composition);
            window = new MainWindow { DataContext = model };
            window.Show();
            SharedUi.Settle(window);

            // The window owns one binding per window action, built from the applier rather than from XAML.
            Assert.Equal(4, window.KeyBindings.Count);
            Assert.Equal(
                [
                    new KeyGesture(Key.OemTilde, KeyModifiers.Control),
                    new KeyGesture(Key.P, KeyModifiers.Control),
                    new KeyGesture(Key.P, KeyModifiers.Control | KeyModifiers.Shift),
                    new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Shift),
                ],
                window.KeyBindings.Select(static binding => binding.Gesture).ToArray());

            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
            SharedUi.Settle(window);
            var opened = Assert.IsType<PaletteViewModel>(model.Palette);
            Assert.True(opened.IsOpen);
            Assert.Equal(PaletteSource.Files, opened.Source);

            // A rebind reaches the live window: the binding is rebuilt and the old combination no longer opens anything.
            opened.Close();
            SharedUi.Settle(window);
            Assert.Null(model.Palette);

            composition.Preferences.KeymapOverrides = ["window.openFilePalette|Ctrl+Shift+O"];
            SharedUi.Settle(window);
            Assert.Equal(new KeyGesture(Key.O, KeyModifiers.Control | KeyModifiers.Shift), window.KeyBindings[1].Gesture);

            window.KeyPressQwerty(PhysicalKey.P, RawInputModifiers.Control);
            SharedUi.Settle(window);
            Assert.Null(model.Palette);

            window.KeyPressQwerty(PhysicalKey.O, RawInputModifiers.Control | RawInputModifiers.Shift);
            SharedUi.Settle(window);
            Assert.Equal(PaletteSource.Files, Assert.IsType<PaletteViewModel>(model.Palette).Source);
        }
        finally
        {
            window?.Close();
            if (composition is not null)
            {
                await composition.DisposeAsync();
            }

            // The applier holds the composition's preferences: drop the static subscription before the next test binds its own.
            KeymapApplier.Unbind();
            previous.Restore();
            TestTempFiles.DeleteTree(root);
        }
    });

    [Fact]
    public Task The_section_view_turns_a_key_press_on_its_capture_surface_into_a_confirmed_override() => SharedUi.RunAsync(() =>
    {
        var preferences = new AppPreferences();
        KeymapApplier.Bind(preferences);
        var section = new KeymapSettingsSectionViewModel(preferences);
        var view = new KeymapSettingsSectionView { DataContext = section };
        var window = new Window { Width = 720, Height = 1200, Content = view };
        try
        {
            window.Show();
            SharedUi.Settle(window);

            var surface = view.FindControl<Border>("CaptureSurface");
            Assert.NotNull(surface);
            Assert.False(surface.IsVisible);

            // What a click on the row's rebind button does: start that row's capture and put the focus on the surface, so the
            // next key press goes there instead of into the search box.
            var rebind = view.GetVisualDescendants()
                .OfType<Button>()
                .Single(button => string.Equals(
                    AutomationProperties.GetAutomationId(button),
                    "keymap-rebind-" + KeymapActionIds.SaveDocument,
                    StringComparison.Ordinal));
            rebind.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SharedUi.Settle(window);

            Assert.True(surface.IsVisible);
            Assert.True(surface.IsFocused, "the rebind click has to focus the capture surface");
            Assert.True(section.CaptureRow!.IsCapturing);

            window.KeyPressQwerty(PhysicalKey.U, RawInputModifiers.Control);
            SharedUi.Settle(window);
            Assert.Equal("Ctrl+U", section.CaptureGestureText);
            Assert.True(section.HasCapturedGesture);
            Assert.False(section.HasConflict);

            section.ConfirmCaptureCommand.Execute(null);
            Assert.Equal(["view.saveDocument|Ctrl+U"], preferences.KeymapOverrides);
            Assert.False(section.HasCapture);
            Assert.Equal("Ctrl+U", KeymapApplier.DisplayGesture(KeymapActionIds.SaveDocument));
        }
        finally
        {
            window.Close();
            section.Dispose();
            KeymapApplier.Unbind();
        }

        return Task.CompletedTask;
    });
}
