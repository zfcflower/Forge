using Avalonia.Headless;
using Xunit;

namespace Forge.App.Tests;

public sealed class HeadlessSmokeTests
{
    [Fact]
    public async Task Headless_session_runs_on_the_avalonia_dispatcher()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var onUi = await session.Dispatch(() => Avalonia.Threading.Dispatcher.UIThread.CheckAccess(), CancellationToken.None);
        Assert.True(onUi);
    }
}
