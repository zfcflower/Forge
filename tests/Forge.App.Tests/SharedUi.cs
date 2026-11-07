using Avalonia.Headless;
using Xunit;

namespace Forge.App.Tests;

// AvaloniaEdit shares key bindings across editors; their AvaloniaObject owner must remain the same dispatcher.
internal static class SharedUi
{
    private static HeadlessUnitTestSession? session;
    public static HeadlessUnitTestSession Session =>
        session ?? throw new InvalidOperationException("The shared headless session is only available inside the forge-app-ui collection.");
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
