using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Forge.App.Composition;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.IO;
using Forge.App.Logging;
using ZLogger;

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
        Views.NumberField.Install();
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
            viewModel = new MainWindowViewModel(composition) { ShowFirstRunWizardOnStartup = true };
            var window = new MainWindow { DataContext = viewModel };
            viewModel.SettingsOwner = () => window;
            desktop.MainWindow = window;
            // Wave W10c: the install confirmation is written once the main window is actually up — that is the proof the
            // applier's watchdog is waiting for — and a finished swap's backup is cleaned at the same point.
            window.Opened += OnMainWindowOpened;
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

    /// <summary>
    /// Wave W10c's app side, once per start: confirm the update this process was started for, then clean up the backups of
    /// earlier ones. It runs on the thread pool — removing the previous version's directory is file work the window must not
    /// wait for — and the confirmation itself is one small file, so the applier's 60 second window is never at risk.
    /// </summary>
    private static void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.Opened -= OnMainWindowOpened;
        }

        var dataRoot = AppPaths.Create().UserDataDirectory;
        var args = Environment.GetCommandLineArgs();
        _ = Task.Run(() => UpdateApplyStartup.RunAfterMainWindowShown(dataRoot, args));
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
            AppLogs.Shell.ZLogError(exception, $"shutdown failed");
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
