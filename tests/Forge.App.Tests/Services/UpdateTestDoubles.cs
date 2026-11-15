using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Preferences;
using Forge.App.Services;

namespace Forge.App.Tests.Services;

/// <summary>
/// Shared doubles for the update-checker tests: an <see cref="HttpMessageHandler"/> whose responses are keyed by absolute
/// URL (manifest and package, including hand-built bodies and transport-level redirects), an in-memory
/// <see cref="IUiStateStore"/>, a clock a test moves by hand and the factory that builds a service against all of them. The
/// services' tests and the settings-section test use them, so the fake HTTP and UI-state plumbing is written once.
/// </summary>
internal sealed class ScriptedUpdateHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> routes = new(StringComparer.Ordinal);

    /// <summary>Absolute URLs that were requested, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>When true, a package request throws as if the network dropped.</summary>
    public bool ThrowNetworkOnPackage { get; set; }

    public void Manifest(string url, string json) => routes[url] = () => Json(json);

    public void Manifest(string url, HttpStatusCode status) => routes[url] = () => new HttpResponseMessage(status);

    public void Package(string url, byte[] bytes) => Respond(url, () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes),
    });

    /// <summary>A route with a hand-built response, for shapes <see cref="Package"/> cannot express (no Content-Length).</summary>
    public void Respond(string url, Func<HttpResponseMessage> factory) => routes[url] = factory;

    /// <summary>
    /// Models the transport's auto-redirect (<c>SocketsHttpHandler.AllowAutoRedirect</c>): a request for
    /// <paramref name="from"/> reaches <paramref name="to"/> and the caller gets whatever that route serves, so the body the
    /// service hashes is the post-redirect one.
    /// </summary>
    public void Redirect(string from, string to) => routes[from] = () =>
    {
        Requests.Add(to);
        return routes.TryGetValue(to, out var target) ? target() : new HttpResponseMessage(HttpStatusCode.NotFound);
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add(url);
        if (ThrowNetworkOnPackage && url.Contains("/package/", StringComparison.Ordinal))
        {
            throw new HttpRequestException("the network is down");
        }

        return Task.FromResult(routes.TryGetValue(url, out var factory)
            ? factory()
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

/// <summary>An <see cref="IUiStateStore"/> that records what was written and serves what was seeded.</summary>
internal sealed class RecordingUiStateStore : IUiStateStore
{
    private readonly Dictionary<string, UiStateEntry> entries = new(StringComparer.Ordinal);

    public int Puts { get; private set; }

    public void Seed(string key, int version, string json) =>
        entries[key] = new UiStateEntry(key, version, json, DateTimeOffset.UtcNow);

    public Task<UiStateEntry?> GetAsync(string key, CancellationToken ct) =>
        Task.FromResult(entries.TryGetValue(key, out var entry) ? entry : null);

    public Task PutAsync(string key, int version, string json, CancellationToken ct)
    {
        Puts++;
        entries[key] = new UiStateEntry(key, version, json, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct) => Task.FromResult(entries.Remove(key));
}

/// <summary>Manifest bodies and hashes for the update tests.</summary>
internal static class UpdateTestManifest
{
    public const string ManifestUrl = "https://example.test/updates.json";

    public static string HexSha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>A single <c>stable</c> channel offering <paramref name="version"/> for one RID.</summary>
    public static string Stable(string version, string rid, string packageUrl, byte[] payload, string notes = "# Notes") =>
        SingleChannel("stable", version, rid, packageUrl, payload, notes);

    /// <summary>One channel under an arbitrary name, so a test can prove the configured channel is the one that must exist.</summary>
    public static string SingleChannel(
        string channelName,
        string version,
        string rid,
        string packageUrl,
        byte[] payload,
        string notes = "# Notes") =>
        JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["schema"] = 1,
            ["channels"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [channelName] = Channel(version, notes, rid, packageUrl, payload),
            },
        });

    /// <summary>Two channels, so the channel-selection test can tell which package the service asked for.</summary>
    public static string TwoChannels(
        string stableVersion,
        string betaVersion,
        string rid,
        string stableUrl,
        string betaUrl,
        byte[] stablePayload,
        byte[] betaPayload) =>
        JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["schema"] = 1,
            ["channels"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["stable"] = Channel(stableVersion, "stable notes", rid, stableUrl, stablePayload),
                ["beta"] = Channel(betaVersion, "beta notes", rid, betaUrl, betaPayload),
            },
        });

    /// <summary>A stable channel whose package carries an explicit hash and size (for the bad-hash test).</summary>
    public static string StableWithHash(string version, string rid, string packageUrl, string sha256, long sizeBytes) =>
        JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["schema"] = 1,
            ["channels"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["stable"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["version"] = version,
                    ["releasedAt"] = "2026-11-15T00:00:00Z",
                    ["notes"] = "",
                    ["packages"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [rid] = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["url"] = packageUrl,
                            ["sha256"] = sha256,
                            ["sizeBytes"] = sizeBytes,
                        },
                    },
                },
            },
        });

    private static Dictionary<string, object> Channel(string version, string notes, string rid, string url, byte[] payload) =>
        new(StringComparer.Ordinal)
        {
            ["version"] = version,
            ["releasedAt"] = "2026-11-15T00:00:00Z",
            ["notes"] = notes,
            ["packages"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [rid] = Package(url, payload),
            },
        };

    private static Dictionary<string, object> Package(string url, byte[] payload) => new(StringComparer.Ordinal)
    {
        ["url"] = url,
        ["sha256"] = HexSha256(payload),
        ["sizeBytes"] = (long)payload.Length,
    };
}

/// <summary>Builds an <see cref="UpdateService"/> against the shared doubles, so service and settings tests arrange once.</summary>
internal static class UpdateTestService
{
    public static UpdateSandbox Build(
        string currentVersion,
        string channel = "stable",
        string frequency = UpdateSchedule.Never,
        string rid = "win-x64",
        Func<string>? manifestUrl = null,
        TimeProvider? time = null,
        Action<string>? openFolder = null,
        TimeSpan? autoCheckPeriod = null)
    {
        var root = TestTempFiles.NewDirectory("forge-update-tests-");
        var handler = new ScriptedUpdateHandler();
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var uiState = new RecordingUiStateStore();
        var service = new UpdateService(
            http,
            root,
            currentVersion,
            manifestUrl ?? (() => UpdateTestManifest.ManifestUrl),
            () => channel,
            () => frequency,
            uiState,
            rid,
            time,
            openFolder ?? (_ => { }),
            installDirectory: null,
            startApplier: null,
            autoCheckPeriod: autoCheckPeriod);
        return new UpdateSandbox(service, handler, root, uiState);
    }

    /// <summary>
    /// A service whose three update preferences are read live from <paramref name="preferences"/>, the way
    /// <c>AppComposition</c> wires it. The settings-section tests need this: the section writes the preferences and the very
    /// next check (or pump tick) must use the new values without rebuilding the service.
    /// </summary>
    public static UpdateSandbox BuildForPreferences(
        AppPreferences preferences,
        string currentVersion,
        string rid = "win-x64",
        TimeProvider? time = null,
        TimeSpan? autoCheckPeriod = null)
    {
        var root = TestTempFiles.NewDirectory("forge-update-tests-");
        var handler = new ScriptedUpdateHandler();
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var uiState = new RecordingUiStateStore();
        var service = new UpdateService(
            http,
            root,
            currentVersion,
            () => preferences.UpdatesManifestUrl,
            () => preferences.UpdatesChannel,
            () => preferences.UpdatesCheckFrequency,
            uiState,
            rid,
            time,
            _ => { },
            installDirectory: null,
            startApplier: null,
            autoCheckPeriod: autoCheckPeriod);
        return new UpdateSandbox(service, handler, root, uiState);
    }

    public static byte[] SetStableOffer(ScriptedUpdateHandler handler, string version, string rid, out string packageUrl)
    {
        var payload = Encoding.UTF8.GetBytes("package " + version + " payload");
        packageUrl = "https://example.test/package/Forge-" + version + "-" + rid + ".zip";
        handler.Manifest(UpdateTestManifest.ManifestUrl, UpdateTestManifest.Stable(version, rid, packageUrl, payload));
        handler.Package(packageUrl, payload);
        return payload;
    }
}

/// <summary>A clock a test moves by hand, so the automatic schedule is measured instead of waited for.</summary>
internal sealed class UpdateTestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    /// <summary>How many times the pump consulted this clock, so a test can prove a quiet tick actually ran.</summary>
    public int Reads { get; private set; }

    public override DateTimeOffset GetUtcNow()
    {
        Reads++;
        return Now;
    }

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>
/// A clock that moves itself: every read advances it, so a pump tick is due every time without the test waiting for a day.
/// Used to prove that a pump really stopped (a live one would keep checking) instead of merely not being due.
/// </summary>
internal sealed class AutoAdvancingUpdateClock(DateTimeOffset now, TimeSpan step) : TimeProvider
{
    private long ticks = now.UtcTicks;
    private readonly long stepTicks = step.Ticks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Add(ref ticks, stepTicks), TimeSpan.Zero);
}

/// <summary>
/// A clock whose first reads fail, to prove the automatic pump survives an unexpected exception instead of dying quietly on
/// the launch that threw it.
/// </summary>
internal sealed class FaultingUpdateClock(TimeProvider inner, int failures) : TimeProvider
{
    private int remaining = failures;

    public override DateTimeOffset GetUtcNow() =>
        Interlocked.Decrement(ref remaining) >= 0 ? throw new InvalidOperationException("the clock was yanked") : inner.GetUtcNow();
}

/// <summary>The pieces of one arranged update check; disposing stops the service and removes the throw-away staging directory.</summary>
internal sealed class UpdateSandbox : IDisposable
{
    public UpdateSandbox(UpdateService service, ScriptedUpdateHandler handler, string root, RecordingUiStateStore uiState)
    {
        Service = service;
        Handler = handler;
        Root = root;
        UiState = uiState;
    }

    public UpdateService Service { get; }

    public ScriptedUpdateHandler Handler { get; }

    public string Root { get; }

    public RecordingUiStateStore UiState { get; }

    public void Dispose()
    {
        // The service owns its HttpClient and, once started, a background pump; leaving both to the finalizer would leak
        // one connection pool per test in a 600-test namespace.
        Service.Dispose();
        TestTempFiles.DeleteTree(Root);
    }
}
