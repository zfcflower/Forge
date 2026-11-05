using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Forge.App.Composition;
using Forge.App.ViewModels;

namespace Forge.App;

/// <summary>
/// Application entry: builds the composition root, hands the graph to the root view model and owns the shutdown order.
/// The window closes first, then the graph is torn down asynchronously (the extension host child process is killed and
/// waited for), and only then does the desktop lifetime shut down.
/// </summary>
public partial class App : Application
{
    private AppComposition? composition;
    private MainWindowViewModel? viewModel;
    private bool shutdownFinished;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            composition = AppComposition.Create();
            viewModel = new MainWindowViewModel(composition);
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
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
            Dispatcher.UIThread.Post(() => lifetime?.Shutdown());
        }
    }

    /// <summary>
    /// Nothing can be awaited from <see cref="AppDomain.ProcessExit"/>; this is the bounded fallback that keeps an
    /// extension host child from outliving the window when the process is torn down without a shutdown request.
    /// </summary>
    private void OnProcessExit(object? sender, EventArgs e) => composition?.Abort();
}
