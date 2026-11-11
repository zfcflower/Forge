using Avalonia.Headless;
using Xunit;

namespace Forge.App.Tests;

[Collection("forge-app-ui")]
public sealed class HeadlessSmokeTests
{
    [Fact]
    public async Task Headless_session_runs_on_the_avalonia_dispatcher()
    {
        var session = SharedUi.Session;
        var onUi = await session.Dispatch(() => Avalonia.Threading.Dispatcher.UIThread.CheckAccess(), CancellationToken.None);
        Assert.True(onUi);
    }
}
