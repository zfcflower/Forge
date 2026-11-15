using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Forge.App.Changes.Markdown;
using Forge.App.Localization;
using Forge.App.Settings;

namespace Forge.App.Views;

/// <summary>
/// The <c>updates</c> section's view. The preferences bind directly; the offered release notes are raw Markdown, so the
/// code-behind renders them through the app's shared <see cref="MarkdownRenderer"/> whenever they change. The view also owns
/// the two things a view model must not: the confirmation dialog in front of an install, and closing the application once the
/// applier is running (windows belong to views — the same rule the provider section follows).
/// </summary>
internal sealed partial class UpdatesSettingsSectionView : UserControl
{
    private UpdatesSettingsSectionViewModel? subscribed;

    public UpdatesSettingsSectionView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void OnDataContextChanged(object? sender, EventArgs e) => Watch(DataContext as UpdatesSettingsSectionViewModel);

    /// <summary>
    /// Moves the one <see cref="INotifyPropertyChanged.PropertyChanged"/> subscription (and the two seams the section calls
    /// back into) to <paramref name="viewModel"/>. Dropping it on detach is what keeps a closed section alive otherwise: a
    /// settings section is rebuilt every time the window opens, and a leaked handler holds the old view and its tree for the
    /// life of the process.
    /// </summary>
    private void Watch(UpdatesSettingsSectionViewModel? viewModel)
    {
        if (ReferenceEquals(subscribed, viewModel))
        {
            return;
        }

        if (subscribed is not null)
        {
            subscribed.PropertyChanged -= OnViewModelPropertyChanged;
            subscribed.ConfirmInstallAsync = null;
            subscribed.RequestExit = null;
        }

        subscribed = viewModel;
        if (subscribed is not null)
        {
            subscribed.PropertyChanged += OnViewModelPropertyChanged;
            subscribed.ConfirmInstallAsync = ConfirmInstallAsync;
            subscribed.RequestExit = RequestExit;
        }

        RenderNotes();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e) => Watch(null);

    /// <summary>
    /// A detach dropped the subscription, so an attach takes it again: the same view can be re-attached with its data context
    /// unchanged (there is no <see cref="StyledElement.DataContextChanged"/> then) and must still render its notes.
    /// </summary>
    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        Watch(DataContext as UpdatesSettingsSectionViewModel);
        RenderNotes();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(UpdatesSettingsSectionViewModel.Notes), System.StringComparison.Ordinal))
        {
            RenderNotes();
        }
    }

    /// <summary>
    /// The install closes the app and replaces its directory, so it asks first: the version is named and the consequence is
    /// spelled out. A host without an owner window cannot ask, and then the answer is no — never an unasked install.
    /// </summary>
    private async Task<bool> ConfirmInstallAsync(string version)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            return false;
        }

        var confirm = new Button { Content = UpdatesStrings.InstallConfirmAction, IsDefault = true };
        var cancel = new Button { Content = ShellStrings.Cancel, IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        var dialog = new Window
        {
            Title = UpdatesStrings.InstallConfirmTitle,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = UpdatesStrings.InstallConfirmMessage(version).Resolve(),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    buttons,
                },
            },
        };

        confirm.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        return await dialog.ShowDialog<bool?>(owner).ConfigureAwait(true) == true;
    }

    /// <summary>
    /// Closes the application through its lifetime, which is the ordered shutdown (window, then the graph, then the process) —
    /// the same path the close button takes, so the applier never races a live extension host.
    /// </summary>
    private static void RequestExit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void RenderNotes()
    {
        if (DataContext is not UpdatesSettingsSectionViewModel { Notes.Length: > 0 } viewModel)
        {
            NotesHost.Content = null;
            return;
        }

        var lines = viewModel.Notes
            .Replace("\r\n", "\n", System.StringComparison.Ordinal)
            .Split('\n');
        NotesHost.Content = MarkdownRenderer.RenderBlocks(MarkdownBlockParser.Parse(lines));
    }
}
