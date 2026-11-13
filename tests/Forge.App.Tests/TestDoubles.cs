using Forge.App.Providers;

namespace Forge.App.Tests;

/// <summary>An HTTP handler for suites that must never reach the network: any request throws instead of dialing out.</summary>
internal sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("no network in this test");
}

/// <summary>The "no provider configured" shape a session-backed view model needs to start.</summary>
internal static class TestProviderServices
{
    /// <summary>Provider services over a blank provider record written into <paramref name="root"/>, with no network handler.</summary>
    public static ProviderServices Unconfigured(string root)
    {
        var settingsPath = Path.Combine(root, "providers-settings.json");
        File.WriteAllText(settingsPath, """{"provider":{"kind":"","baseUrl":""}}""");
        var dataDir = Path.Combine(root, "provider-data");
        Directory.CreateDirectory(dataDir);
        // Fake handler, never used for network here; the workspace does not own it so it is left for reclamation.
        return new ProviderServices(settingsPath, dataDir, new HttpClient(new ThrowingHandler()));
    }

    /// <summary>The same services over a throw-away probe directory that is deleted before this returns.</summary>
    public static ProviderServices UnconfiguredProbe()
    {
        var probe = TestTempFiles.NewDirectory("forge-unconfigured-");
        try
        {
            var settingsPath = Path.Combine(probe, "settings.json");
            File.WriteAllText(settingsPath, """{"provider":{"kind":"","baseUrl":""}}""");
            var dataDir = Path.Combine(probe, "data");
            Directory.CreateDirectory(dataDir);
            return new ProviderServices(settingsPath, dataDir, new HttpClient(new ThrowingHandler()));
        }
        finally
        {
            Directory.Delete(probe, recursive: true);
        }
    }
}
