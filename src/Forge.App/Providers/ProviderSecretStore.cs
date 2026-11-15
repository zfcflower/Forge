using System.Text;
using Forge.App.Logging;
using Forge.App.Services;

namespace Forge.App.Providers;

/// <summary>
/// The credential store behind every provider the user connected by pasting a key. Keys never enter
/// <c>settings.json</c>: that file is meant to be readable, diffable and syncable, so it only holds a reference
/// (<see cref="ProviderAuthDocument.SecretReference"/>) and the store keeps the secret separately, encrypted at rest by
/// <see cref="EncryptedSidecarFile"/> (the mechanics it shares with the MCP token store).
/// <para>The encryption key is a random 32-byte file next to the payload with owner-only permissions on POSIX systems.
/// This protects against accidental disclosure — a screenshot, a pasted settings file, a cloud-synced folder — and not
/// against a local attacker who can already read the user's profile; a DPAPI-backed store would need P/Invoke (which the
/// AOT main program forbids) or a package outside the approved list, and neither trade is worth it here.</para>
/// <para>Every member is thread-safe: the settings page reads while a background save writes.</para>
/// </summary>
internal sealed class ProviderSecretStore
{
    private readonly Lock gate = new();
    private readonly EncryptedSidecarFile file;
    private Dictionary<string, string>? cached;
    private bool unreadable;

    public ProviderSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        file = new EncryptedSidecarFile(directory, SecretsFileName, KeyFileName, "secret store", AppLogs.Providers);
    }

    public const string SecretsFileName = "provider-secrets.json";

    public const string KeyFileName = "provider-secrets.key";

    /// <summary>Where the payload lives, for the diagnostics row in the settings page (the key file is never shown).</summary>
    public string StoragePath => file.SecretsPath;

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

    /// <summary>
    /// Loads and decrypts the store on the caller's thread, which should be the pool. Reading is lazy, and the first reader
    /// would otherwise be the settings page (UI thread) or a provider status line; warming it up front keeps both off disk.
    /// </summary>
    public Task WarmAsync(CancellationToken cancellationToken = default) =>
        Task.Run(EnsureLoaded, cancellationToken);

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
                    "The provider secret store cannot be read, so a new secret would overwrite it: " + StoragePath);
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
                return unreadable ? SecretsFileName + " is unreadable" : null;
            }
        }
    }

    /// <summary>Decrypts every entry once and keeps the plaintext strings; a damaged file leaves the store unreadable for good.</summary>
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
        if (file.Read() is not { } payloads)
        {
            unreadable = true;
            cached = null;
            return false;
        }

        foreach (var (reference, plaintext) in payloads)
        {
            cached[reference] = Encoding.UTF8.GetString(plaintext);
        }

        return true;
    }

    private void Persist()
    {
        // A store that has never written anything has no key file yet; the sidecar creates it before the first payload.
        var payloads = new Dictionary<string, byte[]>(cached!.Count, StringComparer.Ordinal);
        foreach (var (reference, secret) in cached)
        {
            if (secret.Length == 0)
            {
                continue;
            }

            payloads[reference] = Encoding.UTF8.GetBytes(secret);
        }

        file.Write(payloads);
    }
}
