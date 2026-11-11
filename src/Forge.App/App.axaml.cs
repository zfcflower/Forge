using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.ViewModels;
using Forge.IO;

namespace Forge.App;

/// <summary>
/// Application entry: builds the composition root, hands the graph to the root view model and owns the shutdown order.
/// The window closes first, then the graph is torn down asynchronously (the extension host child process is killed and
/// waited for), and only then does the desktop lifetime shut down.
/// </summary>
public partial class App : Application
{
    private AppComposition? composition;

    /// <summary>The single-instance lock on the data root (6.4), held for the life of the process and released last.</summary>
    private IDisposable? instanceLock;
    private MainWindowViewModel? viewModel;
    private bool shutdownFinished;

    public override void Initialize()
    {
        Views.TrimmedTextToolTip.Install();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // One Forge per data root, decided before forge.db is opened: a second instance would race the first on
            // the database and on every workspace. No hand-over to the running instance and no IPC (section 9).
            Loc.EnsureRegistered();
            var dataRoot = AppPaths.Create().UserDataDirectory;
            instanceLock = SingleInstanceLock.TryAcquire(dataRoot);
            if (instanceLock is null)
            {
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = AlreadyRunningWindow();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            composition = AppComposition.Create();
            viewModel = new MainWindowViewModel(composition);
            var window = new MainWindow { DataContext = viewModel };
            viewModel.SettingsOwner = () => window;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += OnShutdownRequested;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (shutdownFinished)
        {
            return;
        }

        e.Cancel = true;
        _ = ShutdownAsync(sender as IClassicDesktopStyleApplicationLifetime);
    }

    private async Task ShutdownAsync(IClassicDesktopStyleApplicationLifetime? lifetime)
    {
        shutdownFinished = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        try
        {
            viewModel?.Dispose();
            if (composition is not null)
            {
                await composition.DisposeAsync().ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.WriteLine("[shell] shutdown failed: " + exception);
        }
        finally
        {
            instanceLock?.Dispose();
            instanceLock = null;
            Dispatcher.UIThread.Post(() => lifetime?.Shutdown());
        }
    }

    /// <summary>The whole UI of a second instance: one line and an OK button; closing it ends the process.</summary>
    private static Window AlreadyRunningWindow()
    {
        var ok = new Button
        {
            Content = ShellStrings.Ok,
            IsDefault = true,
            IsCancel = true,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
        };
        var window = new Window
        {
            Title = LifecycleStrings.AlreadyRunningTitle,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = LifecycleStrings.AlreadyRunningMessage, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    ok,
                },
            },
        };
        ok.Click += (_, _) => window.Close();
        return window;
    }

    /// <summary>
    /// Nothing can be awaited from <see cref="AppDomain.ProcessExit"/>; this is the bounded fallback that keeps an
    /// extension host child from outliving the window when the process is torn down without a shutdown request.
    /// </summary>
    private void OnProcessExit(object? sender, EventArgs e) => composition?.Abort();
}
