using Forge.App.Composition;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.Mcp;
using Xunit;

namespace Forge.App.Tests.Mcp;

/// <summary>
/// The app's encrypted MCP token store: tokens round-trip through <c>mcp-secrets.json</c> + <c>mcp-secrets.key</c>, a missing
/// entry reads as null, and the OAuth wiring flag reflects whether the composition injected the store and the browser UI.
/// </summary>
public sealed class McpTokenStoreTests : IDisposable
{
    private readonly string root = TestTempFiles.NewDirectory("forge-mcp-tokens-");

    public void Dispose() => TestTempFiles.DeleteTree(root);

    private McpTokenStore Store() => new(root);

    [Fact]
    public async Task Tokens_round_trip_through_the_encrypted_store()
    {
        var store = Store();
        var tokens = new McpTokenSet("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "Bearer", "mcp", "mcp");
        await store.SaveAsync("http://127.0.0.1:1\u0000mcp", tokens, CancellationToken.None);

        // A second instance reads and decrypts the same files from disk.
        var reloaded = await new McpTokenStore(root).LoadAsync("http://127.0.0.1:1\u0000mcp", CancellationToken.None);

        Assert.NotNull(reloaded);
        Assert.Equal(tokens, reloaded);
        Assert.True(File.Exists(Path.Combine(root, McpTokenStore.SecretsFileName)));
        Assert.True(File.Exists(Path.Combine(root, McpTokenStore.KeyFileName)));
    }

    [Fact]
    public async Task A_missing_entry_reads_as_null_and_delete_removes_it()
    {
        var store = Store();
        Assert.Null(await store.LoadAsync("missing", CancellationToken.None));

        await store.SaveAsync("key", new McpTokenSet("access", null, null, null, null, null), CancellationToken.None);
        Assert.NotNull(await store.LoadAsync("key", CancellationToken.None));

        await store.DeleteAsync("key", CancellationToken.None);
        Assert.Null(await store.LoadAsync("key", CancellationToken.None));
    }

    [Fact]
    public async Task Registration_fields_round_trip_with_the_tokens()
    {
        var tokens = new McpTokenSet(
            "access",
            "refresh",
            DateTimeOffset.UtcNow.AddHours(1),
            "Bearer",
            "mcp",
            "mcp",
            ClientId: "client-abc",
            TokenEndpoint: "https://as.example/token");
        await Store().SaveAsync("key", tokens, CancellationToken.None);

        var reloaded = await new McpTokenStore(root).LoadAsync("key", CancellationToken.None);

        Assert.Equal(tokens, reloaded);
        // Spelled out because the refresh path dies without it: the id the tokens were issued to, not just the secrets.
        Assert.Equal("client-abc", reloaded!.ClientId);
        Assert.Equal("https://as.example/token", reloaded.TokenEndpoint);
    }

    [Fact]
    public async Task No_token_field_is_written_in_plaintext()
    {
        await Store().SaveAsync(
            "key",
            new McpTokenSet(
                "access-secret-value",
                "refresh-secret-value",
                null,
                "Bearer",
                "mcp",
                "mcp",
                ClientId: "client-secret-value",
                TokenEndpoint: "https://as.example/token"),
            CancellationToken.None);

        var contents = await File.ReadAllTextAsync(Path.Combine(root, McpTokenStore.SecretsFileName), CancellationToken.None);

        Assert.DoesNotContain("access-secret-value", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-secret-value", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret-value", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("https://as.example/token", contents, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_is_persisted_for_a_second_instance()
    {
        var store = Store();
        await store.SaveAsync("gone", new McpTokenSet("access", null, null, null, null, null), CancellationToken.None);
        await store.SaveAsync("kept", new McpTokenSet("access-2", null, null, null, null, null), CancellationToken.None);

        await store.DeleteAsync("gone", CancellationToken.None);

        var reloaded = new McpTokenStore(root);
        Assert.Null(await reloaded.LoadAsync("gone", CancellationToken.None));
        Assert.Equal("access-2", (await reloaded.LoadAsync("kept", CancellationToken.None))!.AccessToken);
    }

    [Fact]
    public async Task A_damaged_store_refuses_a_save_instead_of_overwriting_what_is_left()
    {
        TestTempFiles.WriteFile(root, McpTokenStore.SecretsFileName, """{"version":1,"entries":{}}""");
        TestTempFiles.WriteFile(root, McpTokenStore.KeyFileName, "short");
        var store = Store();

        Assert.Null(await store.LoadAsync("key", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAsync("key", new McpTokenSet("access", null, null, null, null, null), CancellationToken.None).AsTask());
        await store.DeleteAsync("key", CancellationToken.None);
    }

    [Fact]
    public async Task Concurrent_saves_and_loads_keep_every_entry_and_a_readable_file()
    {
        var store = Store();
        const int count = 24;
        await Task.WhenAll(Enumerable.Range(0, count).Select(index => Task.Run(async () =>
        {
            for (var repeat = 0; repeat < 3; repeat++)
            {
                await store.SaveAsync(
                    "server-" + index,
                    new McpTokenSet("access-" + index, "refresh-" + index, null, "Bearer", "mcp", "mcp"),
                    CancellationToken.None);
                Assert.NotNull(await store.LoadAsync("server-" + index, CancellationToken.None));
            }
        })));

        var reloaded = new McpTokenStore(root);
        for (var index = 0; index < count; index++)
        {
            var tokens = await reloaded.LoadAsync("server-" + index, CancellationToken.None);
            Assert.NotNull(tokens);
            Assert.Equal("access-" + index, tokens.AccessToken);
        }
    }

    [Fact]
    public async Task The_services_wiring_reports_whether_oauth_is_available()
    {
        var globalPath = Path.Combine(root, "global", "mcp.json");
        TestTempFiles.WriteFile(root, "global/mcp.json", """{"mcpServers":{}}""");
        await using var preferences = new PreferencesStore(Path.Combine(root, "settings.json"));

        await using var withoutOAuth = McpServices.Create(new McpConfigStore(globalPath, null), preferences, null, null);
        Assert.False(withoutOAuth.CanSignIn);

        await using var withOAuth = McpServices.Create(
            new McpConfigStore(globalPath, null),
            preferences,
            null,
            null,
            tokenStore: Store(),
            oauthUi: new McpOAuthUi());
        Assert.True(withOAuth.CanSignIn);
    }
}
