using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.App.Logging;
using Forge.Mcp;
using ZLogger;

namespace Forge.App.Services;

/// <summary>
/// The MCP OAuth token store: <see cref="IMcpTokenStore"/> over an AES-GCM sidecar-key file pair, exactly the pattern
/// <c>ProviderSecretStore</c> uses — both sit on <see cref="EncryptedSidecarFile"/>, and each keeps its own file names and its
/// own reference namespace. Tokens never enter <c>mcp.json</c>.
/// <para>Keys are the stable (server URL origin, resource) identifiers the manager derives; one server's tokens never leak
/// into another's. Every member is thread-safe: a background refresh writes while the settings page reads.</para>
/// </summary>
internal sealed class McpTokenStore : IMcpTokenStore
{
    private readonly Lock gate = new();
    private readonly EncryptedSidecarFile file;
    private Dictionary<string, McpTokenSet>? cached;
    private bool unreadable;

    public McpTokenStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        file = new EncryptedSidecarFile(directory, SecretsFileName, KeyFileName, "MCP token store", AppLogs.Instruments);
    }

    public const string SecretsFileName = "mcp-secrets.json";

    public const string KeyFileName = "mcp-secrets.key";

    public string StoragePath => file.SecretsPath;

    public ValueTask<McpTokenSet?> LoadAsync(string serverKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        lock (gate)
        {
            return ValueTask.FromResult(EnsureLoaded() && cached!.TryGetValue(serverKey, out var tokens) ? tokens : null);
        }
    }

    public ValueTask SaveAsync(string serverKey, McpTokenSet tokens, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverKey);
        ArgumentNullException.ThrowIfNull(tokens);
        lock (gate)
        {
            if (!EnsureLoaded())
            {
                throw new InvalidOperationException(
                    "The MCP token store cannot be read, so a new token would overwrite it: " + StoragePath);
            }

            var previous = cached!.TryGetValue(serverKey, out var existing) ? existing : null;
            cached[serverKey] = tokens;
            try
            {
                Persist();
            }
            catch
            {
                if (previous is null)
                {
                    cached.Remove(serverKey);
                }
                else
                {
                    cached[serverKey] = previous;
                }

                throw;
            }

            return ValueTask.CompletedTask;
        }
    }

    public ValueTask DeleteAsync(string serverKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverKey))
        {
            return ValueTask.CompletedTask;
        }

        lock (gate)
        {
            if (EnsureLoaded() && cached!.Remove(serverKey))
            {
                Persist();
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Decrypts every entry once and keeps the typed values; a damaged file leaves the store unreadable for good.</summary>
    private bool EnsureLoaded()
    {
        if (cached is not null)
        {
            return true;
        }

        if (unreadable)
        {
            return false;
        }

        cached = new Dictionary<string, McpTokenSet>(StringComparer.Ordinal);
        if (file.Read() is not { } payloads)
        {
            unreadable = true;
            cached = null;
            return false;
        }

        try
        {
            foreach (var (serverKey, plaintext) in payloads)
            {
                var tokens = JsonSerializer.Deserialize(plaintext, McpTokenJsonContext.Default.McpTokenSet);
                if (tokens is not null)
                {
                    cached[serverKey] = tokens;
                }
            }

            return true;
        }
        catch (JsonException exception)
        {
            // Undecryptable is one thing, unparseable is another, but neither can be written back over safely.
            AppLogs.Instruments.ZLogWarning($"MCP token store cannot be read: {exception.GetType().Name}");
            unreadable = true;
            cached = null;
            return false;
        }
    }

    private void Persist()
    {
        var payloads = new Dictionary<string, byte[]>(cached!.Count, StringComparer.Ordinal);
        foreach (var (serverKey, tokens) in cached)
        {
            payloads[serverKey] = JsonSerializer.SerializeToUtf8Bytes(tokens, McpTokenJsonContext.Default.McpTokenSet);
        }

        file.Write(payloads);
    }
}

/// <summary>One server's tokens as the store serializes them; source-generated for the AOT main program.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(McpTokenSet))]
internal sealed partial class McpTokenJsonContext : JsonSerializerContext
{
}
