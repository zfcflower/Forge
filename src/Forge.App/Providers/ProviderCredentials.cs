using System.Diagnostics;
using System.Security.Cryptography;
using Forge.App.Localization;
using Forge.Abstractions;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>
/// The two sign-in credentials the app supports, and the one place that knows how to obtain them: the Claude subscription
/// the user already has in the local CLI, and the GitHub Copilot login (device code → user token → short-lived session
/// token). API keys are not here — they are read straight from the secret store or the environment.
/// <para>Nothing in this class touches the network until a turn or an explicit sign-in asks it to; <see cref="HasClaudeLogin"/>
/// and <see cref="HasCopilotLogin"/> only look at local state, so a settings row can render without a round trip.</para>
/// </summary>
internal sealed class ProviderCredentials
{
    private readonly HttpClient http;
    private readonly ProviderSecretStore secrets;
    private readonly TimeProvider time;
    private readonly ClaudeSubscriptionCredentials claude;
    private readonly CopilotAuthentication copilot;
    private readonly string claudeCredentialsPath;
    private readonly Lock gate = new();
    private readonly Dictionary<string, CopilotSession> copilotSessions = new(StringComparer.Ordinal);

    public ProviderCredentials(
        HttpClient http,
        ProviderSecretStore secrets,
        string? claudeCredentialsPath = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(secrets);
        this.http = http;
        this.secrets = secrets;
        this.time = time ?? TimeProvider.System;
        this.claudeCredentialsPath = claudeCredentialsPath ?? ClaudeSubscriptionCredentials.DefaultCredentialsPath;
        claude = new ClaudeSubscriptionCredentials(this.claudeCredentialsPath, http, this.time);
        copilot = new CopilotAuthentication(http);
    }

    /// <summary>
    /// True when the Claude Code credentials file holds a usable access token (it may still need a refresh before it is
    /// used). A file that exists but has no token reads as "not signed in": the row must not promise what the turn cannot do.
    /// </summary>
    public bool HasClaudeLogin() => ClaudeSubscriptionCredentials.LooksSignedIn(claudeCredentialsPath);

    public LocText ClaudeLoginSummary() => ProviderStrings.ClaudeSignedInText();

    /// <summary>Reads (and, when close to expiry, refreshes) the Claude access token.</summary>
    public Task<ClaudeSubscription> ReadClaudeAsync(CancellationToken ct) => claude.ReadAsync(ct);

    /// <summary>The secret-store reference that holds the GitHub user token for one Copilot provider.</summary>
    public static string CopilotUserTokenReference(string providerId) => "copilot:" + providerId;

    public bool HasCopilotLogin(string providerId) =>
        secrets.Has(CopilotUserTokenReference(providerId)) || CachedSession(providerId) is not null;

    /// <summary>
    /// The Copilot session token for this provider: reuses the cached one until it is within a minute of expiry, otherwise
    /// exchanges the stored GitHub user token for a new one. Throws the "sign in first" reason when there is no user token.
    /// </summary>
    public async Task<CopilotSession> GetCopilotSessionAsync(string providerId, CancellationToken ct)
    {
        if (CachedSession(providerId) is { } cached)
        {
            return cached;
        }

        var userToken = secrets.Get(CopilotUserTokenReference(providerId));
        if (string.IsNullOrWhiteSpace(userToken))
        {
            throw new ProviderException(ProviderStrings.CopilotSignInRequiredText().Resolve(), retryable: false);
        }

        var session = await copilot.ExchangeAsync(userToken, ct).ConfigureAwait(false);
        StoreSession(providerId, session);
        return session;
    }

    /// <summary>Starts a device-code login; the caller shows the code and link and then polls.</summary>
    public Task<CopilotDeviceCode> StartCopilotLoginAsync(string? host, CancellationToken ct) =>
        copilot.StartAsync(host, ct);

    /// <summary>
    /// Polls one device-code login. Returns <c>null</c> while the user has not authorised yet; on success stores the GitHub
    /// user token in the secret store and returns an exchanged session.
    /// </summary>
    public async Task<CopilotSession?> PollCopilotLoginAsync(
        string providerId,
        CopilotDeviceCode code,
        string? host,
        CancellationToken ct)
    {
        var userToken = await copilot.PollAsync(code, host, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(userToken))
        {
            return null;
        }

        var reference = CopilotUserTokenReference(providerId);
        await Task.Run(() => secrets.Set(reference, userToken), ct).ConfigureAwait(false);
        var session = await copilot.ExchangeAsync(userToken, ct).ConfigureAwait(false);
        StoreSession(providerId, session);
        return session;
    }

    /// <summary>Drops the stored login and the cached session, so the row goes back to "needs sign-in".</summary>
    public async Task ForgetCopilotAsync(string providerId, CancellationToken cancellationToken = default)
    {
        InvalidateCopilot(providerId);
        var reference = CopilotUserTokenReference(providerId);
        await Task.Run(() => secrets.Remove(reference), cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Forgets a cached session after a 401 so the next attempt exchanges a fresh one.</summary>
    public void InvalidateCopilot(string providerId)
    {
        lock (gate)
        {
            copilotSessions.Remove(providerId);
        }
    }

    private CopilotSession? CachedSession(string providerId)
    {
        lock (gate)
        {
            return copilotSessions.TryGetValue(providerId, out var session) && session.IsValidAt(time.GetUtcNow())
                ? session
                : null;
        }
    }

    private void StoreSession(string providerId, CopilotSession session)
    {
        lock (gate)
        {
            copilotSessions[providerId] = session;
        }
    }
}

/// <summary>
/// A provider whose credential is resolved per turn: a subscription token can expire and a Copilot session lives about half
/// an hour, so the concrete provider is built on first use and rebuilt after an authentication failure. A 401 before the
/// stream produced anything is retried once with a fresh credential; after the first event it surfaces, because replaying a
/// half-read turn would duplicate output.
/// </summary>
internal sealed class CredentialedChatProvider : IChatProvider
{
    private readonly ProviderCredentialKind kind;
    private readonly ProviderCredentials credentials;
    private readonly string providerId;
    private readonly string defaultBaseUrl;
    private readonly Func<string, string, ProviderAuthStyle, string?, IChatProvider> build;
    private IChatProvider? inner;

    public CredentialedChatProvider(
        string providerId,
        ProviderCredentialKind kind,
        string defaultBaseUrl,
        ProviderCredentials credentials,
        Func<string, string, ProviderAuthStyle, string?, IChatProvider> build)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(build);
        this.providerId = providerId;
        this.kind = kind;
        this.defaultBaseUrl = defaultBaseUrl ?? string.Empty;
        this.credentials = credentials;
        this.build = build;
    }

    public string Id => providerId;

    public IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, CancellationToken ct) =>
        StreamCoreAsync(request, ct);

    private async IAsyncEnumerable<ChatStreamEvent> StreamCoreAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var provider = await ResolveAsync(ct).ConfigureAwait(false);
        var enumerator = provider.StreamAsync(request, ct).GetAsyncEnumerator(ct);
        try
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (ProviderException exception) when (IsAuthFailure(exception))
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
                Invalidate();
                provider = await ResolveAsync(ct).ConfigureAwait(false);
                enumerator = provider.StreamAsync(request, ct).GetAsyncEnumerator(ct);
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }

            while (moved)
            {
                yield return enumerator.Current;
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<IChatProvider> ResolveAsync(CancellationToken ct)
    {
        if (inner is not null)
        {
            return inner;
        }

        switch (kind)
        {
            case ProviderCredentialKind.ClaudeSubscription:
                var subscription = await credentials.ReadClaudeAsync(ct).ConfigureAwait(false);
                if (!subscription.IsSignedIn)
                {
                    throw new ProviderException(ProviderStrings.ClaudeSignInRequiredText().Resolve(), retryable: false);
                }

                inner = build(
                    subscription.AccessToken,
                    defaultBaseUrl,
                    ProviderAuthStyle.BearerOnly,
                    ClaudeSubscriptionCredentials.IdentityPreamble);
                break;

            case ProviderCredentialKind.GitHubCopilot:
                var session = await credentials.GetCopilotSessionAsync(providerId, ct).ConfigureAwait(false);
                inner = build(session.Token, session.ApiBaseUrl, ProviderAuthStyle.BearerOnly, null);
                break;

            default:
                throw new ProviderException(ProviderStrings.NoCredentialText().Resolve(), retryable: false);
        }

        return inner;
    }

    private void Invalidate()
    {
        inner = null;
        switch (kind)
        {
            case ProviderCredentialKind.GitHubCopilot:
                credentials.InvalidateCopilot(providerId);
                break;
            case ProviderCredentialKind.ClaudeSubscription:
                // The credential reader refreshes on its own next read; nothing to drop here.
                break;
        }

        Trace.TraceWarning("[providers] authentication failed; the credential will be refreshed on the next attempt");
    }

    private static bool IsAuthFailure(ProviderException exception) => exception.StatusCode is 401 or 403;
}
