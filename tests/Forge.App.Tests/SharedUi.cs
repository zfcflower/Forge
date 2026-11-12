using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Xunit;

namespace Forge.App.Tests;

// One headless session for the whole test process, owned by the forge-app-ui collection fixture.
//
// A second session is a second Application on a second UI thread. Controls that outlive their own session — a
// window kept alive by a test, a binding that re-evaluates a {DynamicResource} — are then notified from the
// other session's thread and throw VerifyAccess (KeyBinding.get_Gesture, MediaContextClock.Subscribe from a
// transition), or settle late enough that layout assertions drift. That is why every UI test uses this session
// instead of HeadlessUnitTestSession.StartNew: the collection also serialises them onto that one thread.
internal static class SharedUi
{
    private static HeadlessUnitTestSession? session;

    public static HeadlessUnitTestSession Session =>
        session ?? throw new InvalidOperationException(
            "The shared headless session is only available inside the forge-app-ui collection; mark the test class [Collection(\"forge-app-ui\")].");

    /// <summary>Runs <paramref name="body"/> on the shared UI thread under this suite's 120 s budget.</summary>
    internal static async Task RunAsync(Func<Task> body)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await Session.Dispatch(
            async () =>
            {
                await body();
                return 0;
            },
            budget.Token);
    }

    /// <summary>Pumps the dispatcher and one headless render tick <paramref name="passes"/> times, then drains the queue.</summary>
    internal static void Settle(int passes = 2)
    {
        for (var pass = 0; pass < passes; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Shows <paramref name="content"/> in a window of the given size and returns that window.</summary>
    internal static Window Show(Control content, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        return window;
    }

    /// <summary>
    /// Same as <see cref="Settle(int)"/>, but lays <paramref name="window"/> out explicitly between passes: the layout
    /// manager's own scheduled pass is a dispatcher post that does not reliably run under <c>RunJobs</c> alone.
    /// </summary>
    internal static void Settle(Window window, int passes = 2)
    {
        for (var pass = 0; pass < passes; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    internal static void Start() => session = HeadlessUnitTestSession.StartNew(typeof(App));

    internal static void Stop()
    {
        var current = session;
        session = null;
        current?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

public sealed class SharedHeadlessFixture : IDisposable
{
    public SharedHeadlessFixture() => SharedUi.Start();

    public void Dispose() => SharedUi.Stop();
}
