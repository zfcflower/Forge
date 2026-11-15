using System.Security.Cryptography;
using System.Text;
using Forge.App.Services;
using Xunit;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// The encrypted sidecar pair <see cref="McpTokenStore"/> and <c>ProviderSecretStore</c> share: what the crypto and the file
/// pair promise — a payload round-trips, the key is a random 32-byte owner-only file, and a damaged store reads as damaged
/// instead of as empty (an empty store would let the next save overwrite what is left). The stores' own tests cover what they
/// add on top of this: their reference namespaces and the "never write over a damaged file" policy.
/// </summary>
public sealed class EncryptedSidecarFileTests : IDisposable
{
    private const string Secrets = "sidecar-secrets.json";
    private const string Key = "sidecar-secrets.key";

    private readonly string root = TestTempFiles.NewDirectory("forge-sidecar-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    // The sidecar's warnings go to the store's own log category; a test only cares that it has one.
    private static readonly Microsoft.Extensions.Logging.ILogger Log =
        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private EncryptedSidecarFile Store() => new(root, Secrets, Key, "sidecar store", Log);

    private static byte[] Payload(string value) => Encoding.UTF8.GetBytes(value);

    [Fact]
    public void Entries_round_trip_through_the_file_and_an_empty_payload_is_dropped()
    {
        var file = Store();
        file.Write(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["a"] = Payload("secret-a"),
            ["empty"] = [],
        });

        var read = Store().Read();

        Assert.NotNull(read);
        Assert.Equal("secret-a", Encoding.UTF8.GetString(Assert.Single(read).Value));
        Assert.DoesNotContain("secret-a", File.ReadAllText(Path.Combine(root, Secrets)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_file_reads_as_an_empty_store()
    {
        Assert.Empty(Assert.IsType<Dictionary<string, byte[]>>(Store().Read()));
    }

    [Fact]
    public void The_key_sidecar_is_a_32_byte_owner_only_file()
    {
        Store().Write(new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["a"] = Payload("secret") });
        var keyPath = Path.Combine(root, Key);

        Assert.Equal(32, new FileInfo(keyPath).Length);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));
        }
    }

    [Fact]
    public void A_key_sidecar_of_the_wrong_length_reads_as_damaged_without_touching_the_payload()
    {
        Store().Write(new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["a"] = Payload("secret") });
        var secretsPath = Path.Combine(root, Secrets);
        var before = File.ReadAllBytes(secretsPath);
        TestTempFiles.WriteFile(root, Key, "short");

        Assert.Null(Store().Read());
        Assert.Equal(before, File.ReadAllBytes(secretsPath));
    }

    [Fact]
    public void A_payload_that_is_not_json_reads_as_damaged()
    {
        const string broken = "{ this is not json";
        TestTempFiles.WriteFile(root, Secrets, broken);

        Assert.Null(Store().Read());
        Assert.Equal(broken, File.ReadAllText(Path.Combine(root, Secrets)));
    }

    [Fact]
    public void A_payload_encrypted_with_another_key_reads_as_damaged_and_survives_the_rotation()
    {
        Store().Write(new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["a"] = Payload("secret-a") });
        var keyPath = Path.Combine(root, Key);
        var secretsPath = Path.Combine(root, Secrets);
        var originalKey = File.ReadAllBytes(keyPath);
        var originalSecrets = File.ReadAllBytes(secretsPath);

        // The sidecar key was rotated or lost: the payload is still there, but this process cannot read it.
        File.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(originalKey.Length));
        Assert.Null(Store().Read());
        Assert.Equal(originalSecrets, File.ReadAllBytes(secretsPath));

        // With the right key back, the untouched payload still decrypts.
        File.WriteAllBytes(keyPath, originalKey);
        var recovered = Store().Read();
        Assert.NotNull(recovered);
        Assert.Equal("secret-a", Encoding.UTF8.GetString(recovered["a"]));
    }

    [Fact]
    public void Concurrent_writes_and_reads_keep_the_file_readable()
    {
        var file = Store();

        Parallel.For(0, 24, index =>
        {
            file.Write(new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["key"] = Payload("value-" + index) });
            Assert.NotNull(file.Read());
        });

        // Whole-file writes mean the last one wins; what must never happen is a torn file that reads as damaged.
        var read = Store().Read();
        Assert.NotNull(read);
        Assert.StartsWith("value-", Encoding.UTF8.GetString(read["key"]), StringComparison.Ordinal);
    }
}
