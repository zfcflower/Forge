using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.IO;

namespace Forge.App.Providers;

/// <summary>
/// The credential store behind every provider the user connected by pasting a key. Keys never enter
/// <c>settings.json</c>: that file is meant to be readable, diffable and syncable, so it only holds a reference
/// (<see cref="ProviderAuthDocument.SecretReference"/>) and the store keeps the secret separately, encrypted at rest.
/// <para>The encryption key is a random 32-byte file next to the payload with owner-only permissions on POSIX systems.
/// This protects against accidental disclosure — a screenshot, a pasted settings file, a cloud-synced folder — and not
/// against a local attacker who can already read the user's profile; a DPAPI-backed store would need P/Invoke (which the
/// AOT main program forbids) or a package outside the approved list, and neither trade is worth it here.</para>
/// <para>Every member is thread-safe: the settings page reads while a background save writes.</para>
/// </summary>
internal sealed class ProviderSecretStore
{
    /// <summary>AES-GCM tag length in bytes.</summary>
    private const int TagLength = 16;

    private const int NonceLength = 12;

    private const int KeyLength = 32;

    private static readonly byte[] Empty = [];

    private readonly Lock gate = new();
    private readonly string secretsPath;
    private readonly string keyPath;
    private Dictionary<string, string>? cached;
    private byte[]? key;
    private bool unreadable;

    public ProviderSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        secretsPath = Path.Combine(directory, SecretsFileName);
        keyPath = Path.Combine(directory, KeyFileName);
    }

    public const string SecretsFileName = "provider-secrets.json";

    public const string KeyFileName = "provider-secrets.key";

    /// <summary>Where the payload lives, for the diagnostics row in the settings page (the key file is never shown).</summary>
    public string StoragePath => secretsPath;

    /// <summary>The secret behind <paramref name="reference"/>, or <c>null</c> when it is absent or the store is broken.</summary>
    public string? Get(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        lock (gate)
        {
            if (!EnsureLoaded())
            {
                return null;
            }

            return cached!.TryGetValue(reference, out var secret) && secret.Length > 0 ? secret : null;
        }
    }

    public bool Has(string? reference) => Get(reference) is not null;

    /// <summary>Stores (or replaces) one secret. Runs on the caller's thread — call it from a background path.</summary>
    public void Set(string reference, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(secret);

        lock (gate)
        {
            if (!EnsureLoaded())
            {
                throw new InvalidOperationException(
                    "The provider secret store cannot be read, so a new secret would overwrite it: " + secretsPath);
            }

            var previous = cached!.TryGetValue(reference, out var existing) ? existing : null;
            cached[reference] = secret;
            try
            {
                Persist();
            }
            catch
            {
                // Keep memory and disk in step: a failed write must not leave a secret that appears stored.
                if (previous is null)
                {
                    cached.Remove(reference);
                }
                else
                {
                    cached[reference] = previous;
                }

                throw;
            }
        }
    }

    /// <summary>Drops one secret; returns whether it was there. Runs on the caller's thread.</summary>
    public bool Remove(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        lock (gate)
        {
            if (!EnsureLoaded() || !cached!.Remove(reference))
            {
                return false;
            }

            Persist();
            return true;
        }
    }

    /// <summary>Why the store is unusable, or <c>null</c> when it is fine; the settings page shows it instead of "no key".</summary>
    public string? Problem
    {
        get
        {
            lock (gate)
            {
                EnsureLoaded();
                return unreadable ? "provider-secrets.json is unreadable" : null;
            }
        }
    }

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

        cached = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(secretsPath))
        {
            return true;
        }

        try
        {
            key = LoadOrCreateKey();
            using var stream = File.OpenRead(secretsPath);
            var document = JsonSerializer.Deserialize(stream, ProvidersJsonContext.Default.SecretsDocument);
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
                if (plaintext is { Length: > 0 })
                {
                    cached[reference] = Encoding.UTF8.GetString(plaintext);
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                              FormatException or CryptographicException or NotSupportedException)
        {
            // A damaged store is never silently treated as "no keys": that would let a save overwrite what is left.
            unreadable = true;
            cached = null;
            Trace.TraceWarning("[providers] secret store cannot be read: " + exception.GetType().Name);
            return false;
        }
    }

    /// <summary>The AES key, created on first use. Never logged, never written anywhere but the key file.</summary>
    private byte[] LoadOrCreateKey()
    {
        if (key is { Length: KeyLength })
        {
            return key;
        }

        if (File.Exists(keyPath))
        {
            var existing = File.ReadAllBytes(keyPath);
            if (existing.Length != KeyLength)
            {
                throw new FormatException("provider-secrets.key must be " + KeyLength + " bytes.");
            }

            return key = existing;
        }

        var created = RandomNumberGenerator.GetBytes(KeyLength);
        var directory = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllBytes(keyPath, created);
        RestrictToOwner(keyPath);
        return key = created;
    }

    private void Persist()
    {
        var entries = new Dictionary<string, SecretEntryDocument>(StringComparer.Ordinal);
        foreach (var (reference, secret) in cached!)
        {
            if (secret.Length == 0)
            {
                continue;
            }

            var plaintext = Encoding.UTF8.GetBytes(secret);
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(NonceLength);
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[TagLength];
                Encrypt(nonce, plaintext, ciphertext, tag);
                entries[reference] = new SecretEntryDocument
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

        var document = new SecretsDocument { Version = SecretsDocument.CurrentVersion, Entries = entries };
        var json = JsonSerializer.SerializeToUtf8Bytes(document, ProvidersJsonContext.Default.SecretsDocument);
        AtomicFile.WriteAllBytes(secretsPath, json);
        RestrictToOwner(secretsPath);
    }

    /// <summary>
    /// Owner-only permissions where the platform has them. Windows has no per-file equivalent without P/Invoke; the file
    /// already sits inside the user's profile directory, which is the protection this store actually relies on.
    /// </summary>
    private static void RestrictToOwner(string path)
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
            Trace.TraceWarning("[providers] could not restrict secret file permissions");
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
            throw new CryptographicException("A provider secret entry has an unexpected nonce or tag length.");
        }

        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key ?? Empty, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
