using System.Text;
using Forge.App.Providers;
using Xunit;

namespace Forge.App.Tests.Providers;

/// <summary>
/// The encrypted credential store: a pasted key survives a restart, a damaged store is reported instead of looking empty,
/// and the plaintext never reaches the file. It protects against disclosure and sync — not against a local attacker — so the
/// tests assert what it actually promises.
/// </summary>
public sealed class ProviderSecretStoreTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-provider-secrets-");


    [Fact]
    public void A_secret_survives_a_new_store_instance_and_never_appears_in_the_file()
    {
        const string secret = "sk-test-not-a-real-credential";
        var store = new ProviderSecretStore(root);
        store.Set("provider:deepseek", secret);

        Assert.Equal(secret, store.Get("provider:deepseek"));
        Assert.Equal(secret, new ProviderSecretStore(root).Get("provider:deepseek"));
        Assert.DoesNotContain(secret, File.ReadAllText(store.StoragePath), StringComparison.Ordinal);
        Assert.Equal(32, new FileInfo(Path.Combine(root, ProviderSecretStore.KeyFileName)).Length);
        Assert.Null(new ProviderSecretStore(root).Problem);
    }

    [Fact]
    public void Removing_a_secret_leaves_the_other_entries_alone()
    {
        var store = new ProviderSecretStore(root);
        store.Set("a", "one");
        store.Set("b", "two");

        Assert.True(store.Remove("a"));
        Assert.False(store.Remove("a"));
        Assert.Null(store.Get("a"));
        Assert.Equal("two", new ProviderSecretStore(root).Get("b"));
    }

    [Fact]
    public void A_tampered_payload_makes_the_store_report_a_problem_instead_of_looking_empty()
    {
        var store = new ProviderSecretStore(root);
        store.Set("provider:x", "secret-value");
        var document = File.ReadAllText(store.StoragePath);
        var start = document.IndexOf("\"data\": \"", StringComparison.Ordinal) + 9;
        var end = document.IndexOf('"', start);
        var flip = document[start] == 'A' ? 'B' : 'A';
        File.WriteAllText(
            store.StoragePath,
            document[..start] + flip + document[(start + 1)..]);

        var reopened = new ProviderSecretStore(root);

        Assert.Null(reopened.Get("provider:x"));
        Assert.NotNull(reopened.Problem);
    }

    [Fact]
    public void A_write_to_an_unreadable_store_is_refused_instead_of_overwriting_it()
    {
        var store = new ProviderSecretStore(root);
        store.Set("provider:y", "value");
        // Break the key: a key of the wrong length is the one failure the store cannot recover from.
        File.WriteAllBytes(Path.Combine(root, ProviderSecretStore.KeyFileName), Encoding.UTF8.GetBytes("short"));

        var reopened = new ProviderSecretStore(root);

        Assert.NotNull(reopened.Problem);
        Assert.Throws<InvalidOperationException>(() => reopened.Set("provider:z", "other"));
    }

    public void Dispose()
    {
        TestTempFiles.DeleteQuietly(root);
    }
}
