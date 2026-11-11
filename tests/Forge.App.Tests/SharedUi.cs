using Avalonia.Headless;
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
