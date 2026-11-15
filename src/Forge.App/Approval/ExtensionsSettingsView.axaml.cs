using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Forge.App.Localization;

namespace Forge.App.Approval;

/// <summary>
/// The settings section's body. It reads the extension registry, the staged drafts and the rules when it is first shown and
/// again every time the settings page is opened (the section stays in the visual tree while the panel is hidden, so
/// visibility - not attachment - is what "opening" means here). The read itself runs on the background inside the view
/// model; the view only asks for it. It also owns the local-zip file picker because only a view can reach the storage
/// provider.
/// </summary>
public partial class ExtensionsSettingsView : UserControl
{
    public ExtensionsSettingsView()
    {
        InitializeComponent();
        PropertyChanged += OnPropertyChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestRefresh();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        RequestRefresh();
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // "Opened" is a visibility change of this control or of a settings panel above it; the property name is compared
        // instead of the AvaloniaProperty because IsEffectivelyVisible's property field is not public.
        if (string.Equals(e.Property.Name, "IsEffectivelyVisible", StringComparison.Ordinal) ||
            e.Property == IsVisibleProperty)
        {
            RequestRefresh();
        }
    }

    private void RequestRefresh()
    {
        if (!IsEffectivelyVisible || DataContext is not ExtensionsSettingsSectionViewModel viewModel)
        {
            return;
        }

        _ = viewModel.RefreshAsync(CancellationToken.None);
    }

    private async void OnInstallLocalZipClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExtensionsSettingsSectionViewModel viewModel ||
            TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        IReadOnlyList<IStorageFile> picked;
        try
        {
            picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.Instance[ExtensionsStrings.SettingsInstallLocalZip],
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }],
            }).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or
                                              NotSupportedException or UnauthorizedAccessException)
        {
            return;
        }

        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        await viewModel.InstallLocalZipAsync(path, CancellationToken.None).ConfigureAwait(true);
    }
}
