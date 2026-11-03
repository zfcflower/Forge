using System.Text.Json;
using System.Text.Json.Serialization;

namespace Forge.ExtensionHost;

/// <summary>
/// Test seam inside <c>plugin.execute</c>. <see cref="AfterEntryCapturedAsync"/> runs after the request resolved its
/// plugin entry and before it waits for that plugin's gate, so an integration test can change the plugin directory and
/// refresh while the request already holds the earlier entry.
/// </summary>
internal interface IHostTestHook
{
    ValueTask AfterEntryCapturedAsync(string pluginId, CancellationToken ct);
}

/// <summary>
/// File-protocol implementation of <see cref="IHostTestHook"/>, activated by the <see cref="EnvironmentVariable"/>
/// environment variable of the host process. Its value names a JSON file
/// <c>{"pipe":"&lt;pipe name&gt;","directory":"&lt;dir&gt;"}</c>; the hook only activates for the host whose pipe name
/// matches, so a host started by a concurrently running test is never paused. A captured request writes
/// <c>&lt;dir&gt;/&lt;n&gt;.captured</c> (containing the plugin id) and then waits for <c>&lt;dir&gt;/&lt;n&gt;.release</c>,
/// bounded so a forgotten release can never wedge the host.
/// </summary>
internal sealed class FileHostTestHook(string directory) : IHostTestHook
{
    public const string EnvironmentVariable = "FORGE_HOST_TEST_HOOK";

    internal static readonly TimeSpan ReleaseBound = TimeSpan.FromSeconds(180);

    private int sequence;

    /// <summary>Marker that makes the next captured request wait; without it the hook lets every request through.</summary>
    public string ArmPath => Path.Combine(directory, "armed");

    /// <summary>Returns the hook for this host, or <c>null</c> when no hook is configured for its pipe name.</summary>
    public static FileHostTestHook? Activate(string pipeName)
    {
        var configurationPath = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            return null;
        }

        try
        {
            if (!File.Exists(configurationPath))
            {
                return null;
            }

            var configuration = JsonSerializer.Deserialize<HookConfiguration>(File.ReadAllText(configurationPath));
            if (configuration is null ||
                string.IsNullOrWhiteSpace(configuration.Directory) ||
                !string.Equals(configuration.Pipe, pipeName, StringComparison.Ordinal))
            {
                return null;
            }

            return new FileHostTestHook(Path.GetFullPath(configuration.Directory));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"forge-extension-host: the test hook '{configurationPath}' is unusable: {exception.Message}");
            return null;
        }
    }

    public async ValueTask AfterEntryCapturedAsync(string pluginId, CancellationToken ct)
    {
        if (!File.Exists(ArmPath))
        {
            return;
        }

        var name = Interlocked.Increment(ref sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var captured = Path.Combine(directory, name + ".captured");
        var release = Path.Combine(directory, name + ".release");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(captured, pluginId, ct).ConfigureAwait(false);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ReleaseBound);
            while (!File.Exists(release))
            {
                await Task.Delay(10, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"forge-extension-host: the test hook could not pause '{pluginId}': {exception.Message}");
        }
    }

    private sealed class HookConfiguration
    {
        [JsonPropertyName("pipe")]
        public string? Pipe { get; set; }

        [JsonPropertyName("directory")]
        public string? Directory { get; set; }
    }
}
