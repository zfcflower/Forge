using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Xunit;

namespace Forge.App.Tests;

[CollectionDefinition("forge-app-ui", DisableParallelization = true)]
public sealed class ForgeAppUiCollection : ICollectionFixture<SharedHeadlessFixture>;

// One headless session for the whole test process, owned by the forge-app-ui collection fixture.
//
// A second session is a second Application on a second UI thread; controls that outlive their own session are then
// notified from the other session's thread and throw VerifyAccess. Every UI test therefore uses this session, and the
// collection serialises them onto its one thread.
internal static class SharedUi
{
    private static HeadlessUnitTestSession? session;

    public static HeadlessUnitTestSession Session =>
        session ?? throw new InvalidOperationException(
            "The shared headless session is only available inside the forge-app-ui collection; mark the test class [Collection(\"forge-app-ui\")].");

    /// <summary>Runs <paramref name="body"/> on the shared UI thread under a 120 s budget.</summary>
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

    /// <summary>Lays <paramref name="window"/> out and ticks the headless renderer <paramref name="passes"/> times, then drains the queue.</summary>
    internal static void Settle(Window window, int passes = 3)
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

    /// <summary>
    /// Stops the session and waits for its dispatcher thread to finish. Must be awaited, never blocked on: xunit v3
    /// disposes the collection fixture on a thread its own synchronization context owns, and blocking there deadlocks
    /// the session's shutdown (the tests all pass, then the process never exits).
    /// </summary>
    internal static async ValueTask StopAsync()
    {
        var current = session;
        session = null;
        if (current is not null)
        {
            await current.DisposeAsync();
        }
    }
}

public sealed class SharedHeadlessFixture : IAsyncLifetime
{
    public ValueTask InitializeAsync()
    {
        SharedUi.Start();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => SharedUi.StopAsync();
}
