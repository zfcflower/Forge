using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.IO;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Forge.App.Services;

/// <summary>
/// The encrypted sidecar file pair behind the app's secret stores: a JSON payload plus a random 32-byte AES-GCM key in a
/// sibling file that is owner-only on POSIX. Keys never enter <c>settings.json</c> or <c>mcp.json</c>, so those stay readable,
/// diffable and syncable; the payload lives here, encrypted at rest.
/// <para>The protection is against accidental disclosure — a screenshot, a pasted settings file, a cloud-synced folder — and not
/// against a local attacker who can already read the user's profile: a DPAPI-backed store would need P/Invoke (which the AOT
/// main program forbids) or a package outside the approved list, and neither trade is worth it here.</para>
/// <para>This type owns the crypto, the key file and the envelope only; the typed values stay with the store that owns them. Two
/// stores share it (<c>McpTokenStore</c>, <c>ProviderSecretStore</c>), each with its own file names and its own reference
/// namespace, so the mechanics of "encrypt, write atomically, refuse to overwrite a damaged file" exist once.</para>
/// <para>Both members are thread-safe on their own: concurrent writes must not mint two keys for one payload, and a reader
/// must never see a half-published file.</para>
/// </summary>
internal sealed class EncryptedSidecarFile
{
    /// <summary>AES-GCM tag length in bytes.</summary>
    private const int TagLength = 16;

    private const int NonceLength = 12;

    private const int KeyLength = 32;

    private static readonly byte[] Empty = [];

    private readonly Lock gate = new();
    private readonly string subject;
    private readonly ILogger log;
    private byte[]? key;

    /// <param name="directory">Where both files live; created on the first write.</param>
    /// <param name="secretsFileName">The payload file's name, e.g. <c>provider-secrets.json</c>.</param>
    /// <param name="keyFileName">The sidecar key's name, e.g. <c>provider-secrets.key</c>.</param>
    /// <param name="subject">How the log names this store, e.g. <c>MCP token store</c>.</param>
    /// <param name="log">The store's own log category, so its warnings stay where the page that shows them expects them.</param>
    public EncryptedSidecarFile(string directory, string secretsFileName, string keyFileName, string subject, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        SecretsPath = Path.Combine(directory, secretsFileName);
        KeyPath = Path.Combine(directory, keyFileName);
        ArgumentNullException.ThrowIfNull(log);
        this.subject = subject;
        this.log = log;
    }

    /// <summary>Where the payload lives, for the diagnostics row in the settings page (the key file is never shown).</summary>
    public string SecretsPath { get; }

    public string KeyPath { get; }

    /// <summary>
    /// Every entry's plaintext, keyed by reference; an empty dictionary when the file does not exist yet. <c>null</c> means the
    /// store is damaged (a broken payload, a key that does not fit it, an unreadable file) — a caller must then treat the store
    /// as unwritable instead of letting the next save overwrite what is left.
    /// </summary>
    public Dictionary<string, byte[]>? Read()
    {
        lock (gate)
        {
            return ReadCore();
        }
    }

    private Dictionary<string, byte[]>? ReadCore()
    {
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (!File.Exists(SecretsPath))
        {
            return payloads;
        }

        try
        {
            key = LoadOrCreateKey();
            using var stream = File.OpenRead(SecretsPath);
            var document = JsonSerializer.Deserialize(stream, EncryptedSidecarJsonContext.Default.SidecarDocument);
            foreach (var (reference, entry) in document?.Entries ?? [])
            {
                if (entry?.Nonce is null || entry.Tag is null || entry.Data is null)
                {
                    continue;
                }

                var plaintext = Decrypt(
                    Convert.FromBase64String(entry.Nonce),
                    Convert.FromBase64String(entry.Tag),
                    Convert.FromBase64String(entry.Data));
                if (plaintext.Length > 0)
                {
                    payloads[reference] = plaintext;
                }
            }

            return payloads;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                              FormatException or CryptographicException or NotSupportedException)
        {
            // A damaged store is never silently treated as "no entries": that would let a save overwrite what is left.
            log.ZLogWarning($"{subject} cannot be read: {exception.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// Encrypts every payload and writes the file atomically, creating the key on the first write. An empty payload is dropped:
    /// there is no secret to protect, and both stores mean "no value" by it. The arrays are zeroed once they are encrypted, so a
    /// caller must not keep the buffer it handed in.
    /// </summary>
    public void Write(IReadOnlyDictionary<string, byte[]> payloads)
    {
        lock (gate)
        {
            WriteCore(payloads);
        }
    }

    private void WriteCore(IReadOnlyDictionary<string, byte[]> payloads)
    {
        key ??= LoadOrCreateKey();
        var entries = new Dictionary<string, SidecarEntry>(StringComparer.Ordinal);
        foreach (var (reference, plaintext) in payloads)
        {
            if (plaintext.Length == 0)
            {
                continue;
            }

            try
            {
                var nonce = RandomNumberGenerator.GetBytes(NonceLength);
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[TagLength];
                Encrypt(nonce, plaintext, ciphertext, tag);
                entries[reference] = new SidecarEntry
                {
                    Nonce = Convert.ToBase64String(nonce),
                    Tag = Convert.ToBase64String(tag),
                    Data = Convert.ToBase64String(ciphertext),
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }

        var document = new SidecarDocument { Version = SidecarDocument.CurrentVersion, Entries = entries };
        var json = JsonSerializer.SerializeToUtf8Bytes(document, EncryptedSidecarJsonContext.Default.SidecarDocument);
        AtomicFile.WriteAllBytes(SecretsPath, json);
        RestrictToOwner(SecretsPath);
    }

    /// <summary>The AES key, created on first use. Never logged, never written anywhere but the key file.</summary>
    private byte[] LoadOrCreateKey()
    {
        if (key is { Length: KeyLength })
        {
            return key;
        }

        if (File.Exists(KeyPath))
        {
            var existing = File.ReadAllBytes(KeyPath);
            if (existing.Length != KeyLength)
            {
                throw new FormatException(Path.GetFileName(KeyPath) + " must be " + KeyLength + " bytes.");
            }

            return key = existing;
        }

        var created = RandomNumberGenerator.GetBytes(KeyLength);
        var directory = Path.GetDirectoryName(KeyPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllBytes(KeyPath, created);
        RestrictToOwner(KeyPath);
        return key = created;
    }

    /// <summary>
    /// Owner-only permissions where the platform has them. Windows has no per-file equivalent without P/Invoke; the file
    /// already sits inside the user's profile directory, which is the protection this store actually relies on.
    /// </summary>
    private void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.ZLogWarning($"could not restrict {subject} file permissions");
        }
    }

    private void Encrypt(byte[] nonce, byte[] plaintext, byte[] ciphertext, byte[] tag)
    {
        using var aes = new AesGcm(key ?? Empty, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
    }

    private byte[] Decrypt(byte[] nonce, byte[] tag, byte[] ciphertext)
    {
        if (nonce.Length != NonceLength || tag.Length != TagLength)
        {
            throw new CryptographicException($"An {subject} entry has an unexpected nonce or tag length.");
        }

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key ?? Empty, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}

/// <summary>The encrypted sidecar payload; references and nonces are not secret, the payloads are.</summary>
internal sealed class SidecarDocument
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("entries")]
    public Dictionary<string, SidecarEntry>? Entries { get; set; }
}

/// <summary>One AES-GCM payload: base64 nonce, tag and ciphertext.</summary>
internal sealed class SidecarEntry
{
    [JsonPropertyName("nonce")]
    public string? Nonce { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SidecarDocument))]
internal sealed partial class EncryptedSidecarJsonContext : JsonSerializerContext
{
}
