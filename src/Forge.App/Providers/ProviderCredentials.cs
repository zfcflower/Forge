using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Forge.App.Localization;
using Forge.Abstractions;
using Forge.Core.Providers;

namespace Forge.App.Providers;

/// <summary>
/// The sign-in credentials the app supports, and the one place that knows how to obtain them: the Claude subscription the
/// user already has in the local CLI, the GitHub Copilot login (device code → user token → short-lived session token), the
/// Kimi For Coding login and the xAI login (device code → access/refresh token bundle), the Meta subscription (device code →
/// identity token → minted API key) and the ChatGPT plan (device code → access/refresh token bundle that names its account).
/// API keys are not here — they are read straight from the secret store or the environment.
/// <para>Nothing in this class touches the network until a turn or an explicit sign-in asks it to; <see cref="HasClaudeLogin"/>,
/// <see cref="HasCopilotLogin"/>, <see cref="HasKimiLogin"/>, <see cref="HasXAiLogin"/>, <see cref="HasMetaLogin"/> and
/// <see cref="HasChatGptLogin"/> only look at local state, so a settings row can render without a round trip.</para>
/// </summary>
internal sealed class ProviderCredentials
{
    private readonly HttpClient http;
    private readonly ProviderSecretStore secrets;
    private readonly TimeProvider time;
    private readonly ClaudeSubscriptionCredentials claude;
    private readonly CopilotAuthentication copilot;
    private readonly KimiAuthentication kimi;
    private readonly XAiAuthentication xai;
    private readonly MetaAuthentication meta;
    private readonly ChatGptAuthentication chatGpt;
    private readonly string claudeCredentialsPath;
    private readonly Lock gate = new();
    private readonly Dictionary<string, CopilotSession> copilotSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KimiTokenBundle> kimiBundles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> kimiRefreshGates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XAiTokenBundle> xaiBundles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> xaiRefreshGates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MetaCredentialBundle> metaBundles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> metaMintGates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatGptTokenBundle> chatGptBundles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> chatGptRefreshGates = new(StringComparer.Ordinal);

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
        kimi = new KimiAuthentication(http, this.time);
        xai = new XAiAuthentication(http, this.time);
        meta = new MetaAuthentication(http, this.time);
        chatGpt = new ChatGptAuthentication(http, this.time);
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

    /// <summary>The secret-store reference that holds the Kimi token bundle for one provider.</summary>
    public static string KimiTokenReference(string providerId) => "kimi:" + providerId;

    public bool HasKimiLogin(string providerId) =>
        secrets.Has(KimiTokenReference(providerId)) || CachedKimiBundle(providerId) is not null;

    /// <summary>
    /// Runs one Kimi For Coding device-code login: start, wait, poll — until the user approves, the code expires or the caller
    /// cancels. <paramref name="progress"/> is handed the user code and link as soon as they exist, because that is what the
    /// dialog has to show while the poll runs. The bundle is stored only once the token response is complete, so a cancelled
    /// or refused login leaves nothing behind.
    /// </summary>
    public async Task<KimiTokenBundle> SignInKimiAsync(
        string providerId,
        Action<KimiDeviceCode>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        // ConfigureAwait(true) on purpose: the dialog awaits this on the UI thread, and progress must be applied there.
        var code = await kimi.StartAsync(ct).ConfigureAwait(true);
        progress?.Invoke(code);
        var interval = code.IntervalSeconds;
        while (true)
        {
            await kimi.WaitAsync(interval, ct).ConfigureAwait(true);
            if (time.GetUtcNow() >= code.ExpiresAt)
            {
                throw new ProviderException(
                    "Kimi: the device authorization expired. Please restart the login.",
                    retryable: false);
            }

            var result = await kimi.PollAsync(code, interval, ct).ConfigureAwait(true);
            interval = result.IntervalSeconds;
            if (result.Bundle is { } bundle)
            {
                await StoreKimiBundleAsync(providerId, bundle, ct).ConfigureAwait(true);
                return bundle;
            }
        }
    }

    /// <summary>
    /// The Kimi access token for this provider, refreshed when it is within a minute of expiry. The bundle's refresh token
    /// rotates, so a refresh rewrites the stored bundle; refreshes are serialized per provider, because two callers using
    /// the same refresh token would have the loser rejected and its 401 would then drop the bundle the winner stored. Throws
    /// the "sign in first" reason when there is no bundle, and drops a refused one so the row goes back to needing a sign-in.
    /// </summary>
    public async Task<string> GetKimiAccessTokenAsync(string providerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        // One refresh at a time per provider; the fast path (a bundle that is still valid) costs one uncontended wait.
        var refreshGate = KimiRefreshGate(providerId);
        await refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read inside the gate: a caller that waited behind a refresh must see the rotated bundle, not the expired one.
            var bundle = CachedKimiBundle(providerId) ?? ReadStoredKimiBundle(providerId);
            if (bundle is null)
            {
                throw new ProviderException(ProviderStrings.KimiSignInRequiredText().Resolve(), retryable: false);
            }

            if (bundle.IsValidAt(time.GetUtcNow()))
            {
                return bundle.AccessToken;
            }

            KimiTokenBundle refreshed;
            try
            {
                refreshed = await kimi.RefreshAsync(bundle.RefreshToken, ct).ConfigureAwait(false);
            }
            catch (ProviderException exception) when (exception.StatusCode is 401 or 403)
            {
                await ForgetRejectedKimiBundleAsync(providerId, bundle.RefreshToken, ct).ConfigureAwait(false);
                throw new ProviderException(
                    ProviderStrings.KimiSignInRequiredText().Resolve(),
                    retryable: false,
                    statusCode: 401);
            }

            await StoreKimiBundleAsync(providerId, refreshed, ct).ConfigureAwait(false);
            return refreshed.AccessToken;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>The one refresh gate for this provider, created on first use and shared by every later caller.</summary>
    private SemaphoreSlim KimiRefreshGate(string providerId)
    {
        lock (gate)
        {
            if (!kimiRefreshGates.TryGetValue(providerId, out var refreshGate))
            {
                refreshGate = new SemaphoreSlim(1, 1);
                kimiRefreshGates[providerId] = refreshGate;
            }

            return refreshGate;
        }
    }

    /// <summary>
    /// Drops the login after a rejected refresh, but only when the store still holds the refresh token that was rejected: a
    /// caller that rotated the bundle while this refresh was on the wire already stored a working credential, and deleting it
    /// would turn a recoverable 401 into a sign-in the user does not actually need.
    /// </summary>
    private async Task ForgetRejectedKimiBundleAsync(
        string providerId,
        string rejectedRefreshToken,
        CancellationToken ct)
    {
        var stored = ReadStoredKimiBundle(providerId);
        if (stored is not null && !string.Equals(stored.RefreshToken, rejectedRefreshToken, StringComparison.Ordinal))
        {
            // Keep the newer bundle and drop only the access token this caller cached, so the next attempt reads it again.
            InvalidateKimi(providerId);
            return;
        }

        await ForgetKimiAsync(providerId, ct).ConfigureAwait(false);
    }

    /// <summary>Drops the stored Kimi login and the cached bundle, so the row goes back to "needs sign-in".</summary>
    public async Task ForgetKimiAsync(string providerId, CancellationToken cancellationToken = default)
    {
        InvalidateKimi(providerId);
        var reference = KimiTokenReference(providerId);
        await Task.Run(() => secrets.Remove(reference), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forgets the cached bundle after a 401 so the next attempt refreshes from the stored refresh token.</summary>
    public void InvalidateKimi(string providerId)
    {
        lock (gate)
        {
            kimiBundles.Remove(providerId);
        }
    }

    /// <summary>The bundle as the store holds it: one JSON value, encrypted like any pasted key.</summary>
    private KimiTokenBundle? ReadStoredKimiBundle(string providerId)
    {
        var stored = secrets.Get(KimiTokenReference(providerId));
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        KimiTokenDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(stored, ProvidersJsonContext.Default.KimiTokenDocument);
        }
        catch (JsonException)
        {
            // An unreadable bundle is "not signed in", never a crash on the settings path.
            return null;
        }

        if (document?.AccessToken is not { Length: > 0 } access
            || document.RefreshToken is not { Length: > 0 } refresh
            || document.ExpiresAt is not { } expiresAt)
        {
            return null;
        }

        return new KimiTokenBundle(access, refresh, expiresAt);
    }

    private KimiTokenBundle? CachedKimiBundle(string providerId)
    {
        lock (gate)
        {
            return kimiBundles.TryGetValue(providerId, out var bundle) ? bundle : null;
        }
    }

    /// <summary>Writes the bundle to the encrypted store and only then to memory, so a failed write leaves nothing cached.</summary>
    private async Task StoreKimiBundleAsync(string providerId, KimiTokenBundle bundle, CancellationToken ct)
    {
        var reference = KimiTokenReference(providerId);
        var document = new KimiTokenDocument
        {
            AccessToken = bundle.AccessToken,
            RefreshToken = bundle.RefreshToken,
            ExpiresAt = bundle.ExpiresAt,
        };
        var json = JsonSerializer.Serialize(document, ProvidersJsonContext.Default.KimiTokenDocument);
        await Task.Run(() => secrets.Set(reference, json), ct).ConfigureAwait(false);
        lock (gate)
        {
            kimiBundles[providerId] = bundle;
        }
    }

    /// <summary>The secret-store reference that holds the xAI token bundle for one provider.</summary>
    public static string XAiTokenReference(string providerId) => "xai:" + providerId;

    public bool HasXAiLogin(string providerId) =>
        secrets.Has(XAiTokenReference(providerId)) || CachedXAiBundle(providerId) is not null;

    /// <summary>
    /// Runs one xAI device-code login: start, wait, poll — until the user approves, the code expires or the caller cancels.
    /// <paramref name="progress"/> is handed the user code and link as soon as they exist, because that is what the dialog
    /// has to show while the poll runs. The bundle is stored only once the token response is complete, so a cancelled or
    /// refused login leaves nothing behind.
    /// </summary>
    public async Task<XAiTokenBundle> SignInXAiAsync(
        string providerId,
        Action<XAiDeviceCode>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        // ConfigureAwait(true) on purpose: the dialog awaits this on the UI thread, and progress must be applied there.
        var code = await xai.StartAsync(ct).ConfigureAwait(true);
        progress?.Invoke(code);
        var interval = code.IntervalSeconds;
        while (true)
        {
            await xai.WaitAsync(interval, ct).ConfigureAwait(true);
            if (time.GetUtcNow() >= code.ExpiresAt)
            {
                throw new ProviderException(
                    "xAI: the device authorization expired. Please restart the login.",
                    retryable: false);
            }

            var result = await xai.PollAsync(code, interval, ct).ConfigureAwait(true);
            interval = result.IntervalSeconds;
            if (result.Bundle is { } bundle)
            {
                await StoreXAiBundleAsync(providerId, bundle, ct).ConfigureAwait(true);
                return bundle;
            }
        }
    }

    /// <summary>
    /// The xAI access token for this provider, refreshed when the five-minute margin is reached. Refreshes are serialized per
    /// provider, because two callers using the same refresh token would have the loser rejected and its 401 would then drop the
    /// bundle the winner stored. Throws the "sign in first" reason when there is no bundle, and drops a refused one so the row
    /// goes back to needing a sign-in.
    /// </summary>
    public async Task<string> GetXAiAccessTokenAsync(string providerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        // One refresh at a time per provider; the fast path (a bundle that is still valid) costs one uncontended wait.
        var refreshGate = XAiRefreshGate(providerId);
        await refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read inside the gate: a caller that waited behind a refresh must see the rotated bundle, not the expired one.
            var bundle = CachedXAiBundle(providerId) ?? ReadStoredXAiBundle(providerId);
            if (bundle is null)
            {
                throw new ProviderException(ProviderStrings.XAiSignInRequiredText().Resolve(), retryable: false);
            }

            if (bundle.IsValidAt(time.GetUtcNow()))
            {
                return bundle.AccessToken;
            }

            XAiTokenBundle refreshed;
            try
            {
                refreshed = await xai.RefreshAsync(bundle.RefreshToken, ct).ConfigureAwait(false);
            }
            catch (ProviderException exception) when (exception.StatusCode is 401 or 403)
            {
                await ForgetRejectedXAiBundleAsync(providerId, bundle.RefreshToken, ct).ConfigureAwait(false);
                throw new ProviderException(
                    ProviderStrings.XAiSignInRequiredText().Resolve(),
                    retryable: false,
                    statusCode: 401);
            }

            await StoreXAiBundleAsync(providerId, refreshed, ct).ConfigureAwait(false);
            return refreshed.AccessToken;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>The one refresh gate for this provider, created on first use and shared by every later caller.</summary>
    private SemaphoreSlim XAiRefreshGate(string providerId)
    {
        lock (gate)
        {
            if (!xaiRefreshGates.TryGetValue(providerId, out var refreshGate))
            {
                refreshGate = new SemaphoreSlim(1, 1);
                xaiRefreshGates[providerId] = refreshGate;
            }

            return refreshGate;
        }
    }

    /// <summary>
    /// Drops the login after a rejected refresh, but only when the store still holds the refresh token that was rejected: a
    /// caller that rotated the bundle while this refresh was on the wire already stored a working credential, and deleting it
    /// would turn a recoverable 401 into a sign-in the user does not actually need.
    /// </summary>
    private async Task ForgetRejectedXAiBundleAsync(
        string providerId,
        string rejectedRefreshToken,
        CancellationToken ct)
    {
        var stored = ReadStoredXAiBundle(providerId);
        if (stored is not null && !string.Equals(stored.RefreshToken, rejectedRefreshToken, StringComparison.Ordinal))
        {
            // Keep the newer bundle and drop only the access token this caller cached, so the next attempt reads it again.
            InvalidateXAi(providerId);
            return;
        }

        await ForgetXAiAsync(providerId, ct).ConfigureAwait(false);
    }

    /// <summary>Drops the stored xAI login and the cached bundle, so the row goes back to "needs sign-in".</summary>
    public async Task ForgetXAiAsync(string providerId, CancellationToken cancellationToken = default)
    {
        InvalidateXAi(providerId);
        var reference = XAiTokenReference(providerId);
        await Task.Run(() => secrets.Remove(reference), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forgets the cached bundle after a 401 so the next attempt refreshes from the stored refresh token.</summary>
    public void InvalidateXAi(string providerId)
    {
        lock (gate)
        {
            xaiBundles.Remove(providerId);
        }
    }

    /// <summary>The bundle as the store holds it: one JSON value, encrypted like any pasted key.</summary>
    private XAiTokenBundle? ReadStoredXAiBundle(string providerId)
    {
        var stored = secrets.Get(XAiTokenReference(providerId));
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        XAiTokenDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(stored, ProvidersJsonContext.Default.XAiTokenDocument);
        }
        catch (JsonException)
        {
            // An unreadable bundle is "not signed in", never a crash on the settings path.
            return null;
        }

        if (document?.AccessToken is not { Length: > 0 } access
            || document.RefreshToken is not { Length: > 0 } refresh
            || document.ExpiresAt is not { } expiresAt)
        {
            return null;
        }

        return new XAiTokenBundle(access, refresh, expiresAt);
    }

    private XAiTokenBundle? CachedXAiBundle(string providerId)
    {
        lock (gate)
        {
            return xaiBundles.TryGetValue(providerId, out var bundle) ? bundle : null;
        }
    }

    /// <summary>Writes the bundle to the encrypted store and only then to memory, so a failed write leaves nothing cached.</summary>
    private async Task StoreXAiBundleAsync(string providerId, XAiTokenBundle bundle, CancellationToken ct)
    {
        var reference = XAiTokenReference(providerId);
        var document = new XAiTokenDocument
        {
            AccessToken = bundle.AccessToken,
            RefreshToken = bundle.RefreshToken,
            ExpiresAt = bundle.ExpiresAt,
        };
        var json = JsonSerializer.Serialize(document, ProvidersJsonContext.Default.XAiTokenDocument);
        await Task.Run(() => secrets.Set(reference, json), ct).ConfigureAwait(false);
        lock (gate)
        {
            xaiBundles[providerId] = bundle;
        }
    }

    /// <summary>The secret-store reference that holds the Meta credential bundle for one provider.</summary>
    public static string MetaTokenReference(string providerId) => "meta:" + providerId;

    public bool HasMetaLogin(string providerId) =>
        secrets.Has(MetaTokenReference(providerId)) || CachedMetaBundle(providerId) is not null;

    /// <summary>
    /// Runs one Meta sign-in: the device flow for an identity token, then the mint that turns it into the API key the model
    /// requests use. <paramref name="progress"/> is handed the user code and link as soon as they exist, and
    /// <paramref name="enablingAccess"/> fires once the user has approved and the mint is about to run — that step is a
    /// second round trip and the dialog has to say so instead of looking stuck. The bundle is stored only after the mint
    /// succeeds, so a cancelled or refused login leaves nothing behind.
    /// </summary>
    public async Task<MetaCredentialBundle> SignInMetaAsync(
        string providerId,
        Action<MetaDeviceCode>? progress,
        Action? enablingAccess,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        // ConfigureAwait(true) on purpose: the dialog awaits this on the UI thread, and progress must be applied there.
        var code = await meta.StartAsync(ct).ConfigureAwait(true);
        progress?.Invoke(code);
        var interval = code.IntervalSeconds;
        string? identityToken = null;
        while (identityToken is null)
        {
            await meta.WaitAsync(interval, ct).ConfigureAwait(true);
            if (time.GetUtcNow() >= code.ExpiresAt)
            {
                throw new ProviderException(
                    "Meta: the device authorization expired. Please restart the login.",
                    retryable: false);
            }

            var result = await meta.PollAsync(code, interval, ct).ConfigureAwait(true);
            interval = result.IntervalSeconds;
            identityToken = result.IdentityToken;
        }

        // The identity token is not the credential: the key it mints is. A failure here (an unfinished account, an expired
        // session) has to surface with Meta's own explanation, so it is not swallowed into "signed in".
        enablingAccess?.Invoke();
        var bundle = await meta.MintAsync(identityToken, ct).ConfigureAwait(true);
        await StoreMetaBundleAsync(providerId, bundle, ct).ConfigureAwait(true);
        return bundle;
    }

    /// <summary>
    /// The Meta API key for this provider, re-minted with the stored identity token when the five-minute margin is reached.
    /// Mints are serialized per provider, because two of them would race to store a key the other has already replaced.
    /// Throws the "sign in first" reason when there is no bundle, and drops a refused one so the row asks for a sign-in again.
    /// </summary>
    public async Task<string> GetMetaApiKeyAsync(string providerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var mintGate = MetaMintGate(providerId);
        await mintGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read inside the gate: a caller that waited behind a mint must see the fresh key, not the expired one.
            var bundle = CachedMetaBundle(providerId) ?? ReadStoredMetaBundle(providerId);
            if (bundle is null)
            {
                throw new ProviderException(ProviderStrings.MetaSignInRequiredText().Resolve(), retryable: false);
            }

            if (bundle.IsValidAt(time.GetUtcNow()))
            {
                return bundle.ApiKey;
            }

            MetaCredentialBundle reminted;
            try
            {
                reminted = await meta.MintAsync(bundle.IdentityToken, ct).ConfigureAwait(false);
            }
            catch (ProviderException exception) when (exception.StatusCode is 401 or 403)
            {
                await ForgetRejectedMetaBundleAsync(providerId, bundle.IdentityToken, ct).ConfigureAwait(false);
                throw new ProviderException(
                    ProviderStrings.MetaSignInRequiredText().Resolve(),
                    retryable: false,
                    statusCode: 401);
            }

            await StoreMetaBundleAsync(providerId, reminted, ct).ConfigureAwait(false);
            return reminted.ApiKey;
        }
        finally
        {
            mintGate.Release();
        }
    }

    /// <summary>The one mint gate for this provider, created on first use and shared by every later caller.</summary>
    private SemaphoreSlim MetaMintGate(string providerId)
    {
        lock (gate)
        {
            if (!metaMintGates.TryGetValue(providerId, out var mintGate))
            {
                mintGate = new SemaphoreSlim(1, 1);
                metaMintGates[providerId] = mintGate;
            }

            return mintGate;
        }
    }

    /// <summary>
    /// Drops the login after a refused mint, but only when the store still holds the identity token that was refused: a
    /// caller that re-minted while this request was on the wire already stored a working key, and deleting it would turn a
    /// recoverable 401 into a sign-in the user does not actually need.
    /// </summary>
    private async Task ForgetRejectedMetaBundleAsync(
        string providerId,
        string rejectedIdentityToken,
        CancellationToken ct)
    {
        var stored = ReadStoredMetaBundle(providerId);
        if (stored is not null && !string.Equals(stored.IdentityToken, rejectedIdentityToken, StringComparison.Ordinal))
        {
            // Keep the newer bundle and drop only the key this caller cached, so the next attempt reads it again.
            InvalidateMeta(providerId);
            return;
        }

        await ForgetMetaAsync(providerId, ct).ConfigureAwait(false);
    }

    /// <summary>Drops the stored Meta login and the cached bundle, so the row goes back to "needs sign-in".</summary>
    public async Task ForgetMetaAsync(string providerId, CancellationToken cancellationToken = default)
    {
        InvalidateMeta(providerId);
        var reference = MetaTokenReference(providerId);
        await Task.Run(() => secrets.Remove(reference), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forgets the cached bundle after a 401 so the next attempt reads the stored identity token again.</summary>
    public void InvalidateMeta(string providerId)
    {
        lock (gate)
        {
            metaBundles.Remove(providerId);
        }
    }

    /// <summary>The bundle as the store holds it: one JSON value, encrypted like any pasted key.</summary>
    private MetaCredentialBundle? ReadStoredMetaBundle(string providerId)
    {
        var stored = secrets.Get(MetaTokenReference(providerId));
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        MetaTokenDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(stored, ProvidersJsonContext.Default.MetaTokenDocument);
        }
        catch (JsonException)
        {
            // An unreadable bundle is "not signed in", never a crash on the settings path.
            return null;
        }

        if (document?.ApiKey is not { Length: > 0 } apiKey
            || document.IdentityToken is not { Length: > 0 } identityToken
            || document.ExpiresAt is not { } expiresAt)
        {
            return null;
        }

        return new MetaCredentialBundle(apiKey, identityToken, expiresAt);
    }

    private MetaCredentialBundle? CachedMetaBundle(string providerId)
    {
        lock (gate)
        {
            return metaBundles.TryGetValue(providerId, out var bundle) ? bundle : null;
        }
    }

    /// <summary>Writes the bundle to the encrypted store and only then to memory, so a failed write leaves nothing cached.</summary>
    private async Task StoreMetaBundleAsync(string providerId, MetaCredentialBundle bundle, CancellationToken ct)
    {
        var reference = MetaTokenReference(providerId);
        var document = new MetaTokenDocument
        {
            ApiKey = bundle.ApiKey,
            IdentityToken = bundle.IdentityToken,
            ExpiresAt = bundle.ExpiresAt,
        };
        var json = JsonSerializer.Serialize(document, ProvidersJsonContext.Default.MetaTokenDocument);
        await Task.Run(() => secrets.Set(reference, json), ct).ConfigureAwait(false);
        lock (gate)
        {
            metaBundles[providerId] = bundle;
        }
    }

    /// <summary>The secret-store reference that holds the ChatGPT plan bundle for one provider.</summary>
    public static string ChatGptTokenReference(string providerId) => "chatgpt:" + providerId;

    public bool HasChatGptLogin(string providerId) =>
        secrets.Has(ChatGptTokenReference(providerId)) || CachedChatGptBundle(providerId) is not null;

    /// <summary>
    /// Runs one ChatGPT plan sign-in: the device code, the poll until the user approves, then the exchange for the token
    /// bundle. <paramref name="progress"/> is handed the user code as soon as it exists, because that is what the dialog has
    /// to show while the poll runs. The bundle is stored only once the exchange is complete, so a cancelled or refused
    /// login leaves nothing behind.
    /// </summary>
    public async Task<ChatGptTokenBundle> SignInChatGptAsync(
        string providerId,
        Action<ChatGptDeviceCode>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        // ConfigureAwait(true) on purpose: the dialog awaits this on the UI thread, and progress must be applied there.
        var code = await chatGpt.StartAsync(ct).ConfigureAwait(true);
        progress?.Invoke(code);
        var interval = code.IntervalSeconds;
        ChatGptPollResult? approval = null;
        while (approval is null)
        {
            await chatGpt.WaitAsync(interval, ct).ConfigureAwait(true);
            if (time.GetUtcNow() >= code.ExpiresAt)
            {
                throw new ProviderException(
                    "ChatGPT: the device authorization expired. Please restart the login.",
                    retryable: false);
            }

            var result = await chatGpt.PollAsync(code, interval, ct).ConfigureAwait(true);
            interval = result.IntervalSeconds;
            if (!result.IsPending)
            {
                approval = result;
            }
        }

        var bundle = await chatGpt
            .ExchangeAsync(approval.AuthorizationCode!, approval.CodeVerifier!, ct)
            .ConfigureAwait(true);
        await StoreChatGptBundleAsync(providerId, bundle, ct).ConfigureAwait(true);
        return bundle;
    }

    /// <summary>
    /// The ChatGPT credential for this provider, refreshed inside the five-minute margin. The account id travels with the
    /// token because every request has to name it; refreshes are serialized per provider, because two callers using the same
    /// refresh token would have the loser rejected and its 401 would then drop the bundle the winner stored. Throws the
    /// "sign in first" reason when there is no bundle, and drops a refused one so the row asks for a sign-in again.
    /// </summary>
    public async Task<ChatGptTokenBundle> GetChatGptTokenAsync(string providerId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var refreshGate = ChatGptRefreshGate(providerId);
        await refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read inside the gate: a caller that waited behind a refresh must see the rotated bundle, not the expired one.
            var bundle = CachedChatGptBundle(providerId) ?? ReadStoredChatGptBundle(providerId);
            if (bundle is null)
            {
                throw new ProviderException(ProviderStrings.ChatGptSignInRequiredText().Resolve(), retryable: false);
            }

            if (bundle.IsValidAt(time.GetUtcNow()))
            {
                return bundle;
            }

            ChatGptTokenBundle refreshed;
            try
            {
                refreshed = await chatGpt.RefreshAsync(bundle.RefreshToken, ct).ConfigureAwait(false);
            }
            catch (ProviderException exception) when (exception.StatusCode is 401 or 403)
            {
                await ForgetRejectedChatGptBundleAsync(providerId, bundle.RefreshToken, ct).ConfigureAwait(false);
                throw new ProviderException(
                    ProviderStrings.ChatGptSignInRequiredText().Resolve(),
                    retryable: false,
                    statusCode: 401);
            }

            await StoreChatGptBundleAsync(providerId, refreshed, ct).ConfigureAwait(false);
            return refreshed;
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>The one refresh gate for this provider, created on first use and shared by every later caller.</summary>
    private SemaphoreSlim ChatGptRefreshGate(string providerId)
    {
        lock (gate)
        {
            if (!chatGptRefreshGates.TryGetValue(providerId, out var refreshGate))
            {
                refreshGate = new SemaphoreSlim(1, 1);
                chatGptRefreshGates[providerId] = refreshGate;
            }

            return refreshGate;
        }
    }

    /// <summary>
    /// Drops the login after a rejected refresh, but only when the store still holds the refresh token that was rejected: a
    /// caller that rotated the bundle while this refresh was on the wire already stored a working credential, and deleting it
    /// would turn a recoverable 401 into a sign-in the user does not actually need.
    /// </summary>
    private async Task ForgetRejectedChatGptBundleAsync(
        string providerId,
        string rejectedRefreshToken,
        CancellationToken ct)
    {
        var stored = ReadStoredChatGptBundle(providerId);
        if (stored is not null && !string.Equals(stored.RefreshToken, rejectedRefreshToken, StringComparison.Ordinal))
        {
            // Keep the newer bundle and drop only the access token this caller cached, so the next attempt reads it again.
            InvalidateChatGpt(providerId);
            return;
        }

        await ForgetChatGptAsync(providerId, ct).ConfigureAwait(false);
    }

    /// <summary>Drops the stored ChatGPT login and the cached bundle, so the row goes back to "needs sign-in".</summary>
    public async Task ForgetChatGptAsync(string providerId, CancellationToken cancellationToken = default)
    {
        InvalidateChatGpt(providerId);
        var reference = ChatGptTokenReference(providerId);
        await Task.Run(() => secrets.Remove(reference), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Forgets the cached bundle after a 401 so the next attempt refreshes from the stored refresh token.</summary>
    public void InvalidateChatGpt(string providerId)
    {
        lock (gate)
        {
            chatGptBundles.Remove(providerId);
        }
    }

    /// <summary>The bundle as the store holds it: one JSON value, encrypted like any pasted key.</summary>
    private ChatGptTokenBundle? ReadStoredChatGptBundle(string providerId)
    {
        var stored = secrets.Get(ChatGptTokenReference(providerId));
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        ChatGptTokenDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(stored, ProvidersJsonContext.Default.ChatGptTokenDocument);
        }
        catch (JsonException)
        {
            // An unreadable bundle is "not signed in", never a crash on the settings path.
            return null;
        }

        if (document?.AccessToken is not { Length: > 0 } access
            || document.RefreshToken is not { Length: > 0 } refresh
            || document.AccountId is not { Length: > 0 } accountId
            || document.ExpiresAt is not { } expiresAt)
        {
            return null;
        }

        return new ChatGptTokenBundle(access, refresh, accountId, expiresAt);
    }

    private ChatGptTokenBundle? CachedChatGptBundle(string providerId)
    {
        lock (gate)
        {
            return chatGptBundles.TryGetValue(providerId, out var bundle) ? bundle : null;
        }
    }

    /// <summary>Writes the bundle to the encrypted store and only then to memory, so a failed write leaves nothing cached.</summary>
    private async Task StoreChatGptBundleAsync(string providerId, ChatGptTokenBundle bundle, CancellationToken ct)
    {
        var reference = ChatGptTokenReference(providerId);
        var document = new ChatGptTokenDocument
        {
            AccessToken = bundle.AccessToken,
            RefreshToken = bundle.RefreshToken,
            AccountId = bundle.AccountId,
            ExpiresAt = bundle.ExpiresAt,
        };
        var json = JsonSerializer.Serialize(document, ProvidersJsonContext.Default.ChatGptTokenDocument);
        await Task.Run(() => secrets.Set(reference, json), ct).ConfigureAwait(false);
        lock (gate)
        {
            chatGptBundles[providerId] = bundle;
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
    private readonly Func<string, string, ProviderAuthStyle, string?, IReadOnlyDictionary<string, string>?, IChatProvider> build;
    private IChatProvider? inner;

    public CredentialedChatProvider(
        string providerId,
        ProviderCredentialKind kind,
        string defaultBaseUrl,
        ProviderCredentials credentials,
        Func<string, string, ProviderAuthStyle, string?, IReadOnlyDictionary<string, string>?, IChatProvider> build)
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
                    ClaudeSubscriptionCredentials.IdentityPreamble,
                    null);
                break;

            case ProviderCredentialKind.GitHubCopilot:
                var session = await credentials.GetCopilotSessionAsync(providerId, ct).ConfigureAwait(false);
                inner = build(session.Token, session.ApiBaseUrl, ProviderAuthStyle.BearerOnly, null, null);
                break;

            case ProviderCredentialKind.KimiCode:
                var accessToken = await credentials.GetKimiAccessTokenAsync(providerId, ct).ConfigureAwait(false);
                inner = build(accessToken, defaultBaseUrl, ProviderAuthStyle.BearerOnly, null, null);
                break;

            case ProviderCredentialKind.XAiSubscription:
                var xaiAccessToken = await credentials.GetXAiAccessTokenAsync(providerId, ct).ConfigureAwait(false);
                inner = build(xaiAccessToken, defaultBaseUrl, ProviderAuthStyle.BearerOnly, null, null);
                break;

            case ProviderCredentialKind.MetaSubscription:
                // The minted key is the whole credential: the Meta models are Responses-only, so nothing else is needed.
                var metaApiKey = await credentials.GetMetaApiKeyAsync(providerId, ct).ConfigureAwait(false);
                inner = build(metaApiKey, defaultBaseUrl, ProviderAuthStyle.BearerOnly, null, null);
                break;

            case ProviderCredentialKind.ChatGptSubscription:
                // A plan is not addressable by its token alone: every request has to name the account it draws from.
                var chatGptBundle = await credentials.GetChatGptTokenAsync(providerId, ct).ConfigureAwait(false);
                var chatGptHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ChatGptAuthentication.ApplyHeaders(chatGptHeaders, chatGptBundle.AccessToken, chatGptBundle.AccountId);
                inner = build(
                    chatGptBundle.AccessToken,
                    defaultBaseUrl,
                    ProviderAuthStyle.BearerOnly,
                    null,
                    chatGptHeaders);
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
            case ProviderCredentialKind.KimiCode:
                // The stored refresh token survives; only the cached access token is dropped so the next attempt refreshes it.
                credentials.InvalidateKimi(providerId);
                break;
            case ProviderCredentialKind.XAiSubscription:
                // The same rule as Kimi: the refresh token stays, the cached access token does not.
                credentials.InvalidateXAi(providerId);
                break;
            case ProviderCredentialKind.MetaSubscription:
                // The minted key stays; only the cached bundle is dropped so the next attempt reads the stored identity token.
                credentials.InvalidateMeta(providerId);
                break;
            case ProviderCredentialKind.ChatGptSubscription:
                // The refresh token stays, the cached access token does not: the next attempt refreshes and re-reads the account.
                credentials.InvalidateChatGpt(providerId);
                break;
            case ProviderCredentialKind.ClaudeSubscription:
                // The credential reader refreshes on its own next read; nothing to drop here.
                break;
        }

        Trace.TraceWarning("[providers] authentication failed; the credential will be refreshed on the next attempt");
    }

    private static bool IsAuthFailure(ProviderException exception) => exception.StatusCode is 401 or 403;
}
