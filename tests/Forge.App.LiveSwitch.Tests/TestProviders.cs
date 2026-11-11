using Forge.App.Providers;

namespace Forge.App.LiveSwitch.Tests;

/// <summary>
/// A provider service over a throw-away directory, for the tests that build a session without the real composition root.
/// It loads the seeded default provider (the catalogue never requires a credential to exist), so the workspace under test
/// gets a real status text instead of a hand-made one.
/// </summary>
internal static class TestProviders
{
    public static ProviderServices Service()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-live-providers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return new ProviderServices(Path.Combine(directory, "settings.json"), directory, http);
    }
}
