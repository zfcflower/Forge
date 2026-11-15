namespace Forge.Mcp;

/// <summary>
/// Persisted OAuth tokens for one MCP server (wave W10d). The library never stores these itself: persistence is injected
/// through <see cref="IMcpTokenStore"/> so the app can keep them in its encrypted secret store (AES-GCM with a sidecar
/// key, the <c>ProviderSecretStore</c> pattern). <see cref="Resource"/> is the RFC 8707 resource indicator the tokens were
/// issued for; <see cref="ExpiresAt"/> is null when the authorization server did not state a lifetime.
/// <see cref="ClientId"/> and <see cref="TokenEndpoint"/> persist the RFC 7591 dynamic registration alongside the tokens,
/// so a refresh after an app restart reuses the original client instead of registering a new one (servers that bind
/// refresh tokens to the issuing client would otherwise reject it).
/// </summary>
public sealed record McpTokenSet(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? TokenType,
    string? Scope,
    string? Resource,
    string? ClientId = null,
    string? TokenEndpoint = null);

/// <summary>
/// Encrypted token persistence for MCP OAuth (wave W10d), injected by the app. The key is a stable identifier derived
/// from the server URL origin plus the resource indicator, so one server's tokens never leak into another's. All methods
/// are async because the app's implementation touches the file system; a missing entry reads as null, never an error.
/// </summary>
public interface IMcpTokenStore
{
    ValueTask<McpTokenSet?> LoadAsync(string serverKey, CancellationToken cancellationToken = default);

    ValueTask SaveAsync(string serverKey, McpTokenSet tokens, CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(string serverKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// The browser side of MCP OAuth (wave W10d), injected by the app. The library never opens a browser on its own: a 401
/// marks the server as needing sign-in, and the flow only starts when the user presses the sign-in action, which calls
/// <see cref="OpenBrowser"/> with the authorization URI. The loopback redirect listener itself lives in the library (BCL).
/// </summary>
public interface IMcpOAuthUi
{
    void OpenBrowser(Uri authorizationUri);
}