namespace Forge.Core.Providers;

/// <summary>Which wire protocol a provider speaks. Everything is one of these three; a vendor is a URL and a header.</summary>
public enum ProviderProtocol
{
    /// <summary>OpenAI Chat Completions (<c>POST {baseUrl}/chat/completions</c>), also spoken by most gateways.</summary>
    OpenAiCompatible,

    /// <summary>Anthropic Messages (<c>POST {baseUrl}/v1/messages</c>).</summary>
    Anthropic,

    /// <summary>
    /// OpenAI Responses (<c>POST {baseUrl}/responses</c>): the item-based protocol with server-side reasoning effort. Only
    /// vendors whose models are reachable through nothing else use it; a vendor that also speaks Chat Completions stays on
    /// <see cref="OpenAiCompatible"/>.
    /// </summary>
    OpenAiResponses,
}

/// <summary>
/// How a provider's credential can be obtained. A provider may offer several: Anthropic takes a Console API key and also
/// the subscription the user is already signed in to with Claude Code, and those are two different ways to reach the same
/// endpoint with two different shapes of credential header.
/// </summary>
[Flags]
public enum ProviderAuthOptions
{
    None = 0,

    /// <summary>A key the user pastes, or the name of an environment variable that holds it.</summary>
    ApiKey = 1,

    /// <summary>The account the user already signed in to in the local <c>claude</c> CLI; usage draws on that subscription.</summary>
    ClaudeSubscription = 2,

    /// <summary>GitHub's device-code flow, exchanged for a short-lived Copilot session token.</summary>
    GitHubCopilot = 4,

    /// <summary>Kimi For Coding's OAuth device-code flow; the access token is used as a bearer token on the coding API.</summary>
    KimiCode = 8,

    /// <summary>
    /// xAI's OAuth device-code flow (<c>auth.x.ai</c>); the resulting access token is the bearer token the grok API takes.
    /// </summary>
    XAiSubscription = 16,

    /// <summary>
    /// Meta's subscription sign-in: its models are reachable through the Responses protocol only, so a key alone does not
    /// reach them.
    /// </summary>
    MetaSubscription = 32,

    /// <summary>
    /// A ChatGPT plan rather than a platform key: the model list comes from the subscription and every request has to name
    /// the account it is drawn from.
    /// </summary>
    ChatGptSubscription = 64,

    /// <summary>
    /// OpenRouter's PKCE sign-in (<c>openrouter.ai/auth</c>): the browser returns an authorization code that is exchanged for
    /// a long-lived API key. The key is an ordinary key, so the credential kind stays <see cref="ApiKey"/>.
    /// </summary>
    OpenRouterOAuth = 128,
}

/// <summary>
/// The second, subscription way in to a vendor that is also reachable with a key: the same product at a different endpoint,
/// sometimes over a different protocol, and — because a plan has no listing endpoint — with seed models of its own. Only a
/// preset whose subscription is genuinely another endpoint carries one; everything else leaves it <c>null</c>.
/// </summary>
/// <param name="Protocol">The wire protocol that endpoint speaks, which can differ from the key endpoint's.</param>
/// <param name="BaseUrl">The endpoint root; the adapter appends its own path (<c>/responses</c> for the Responses protocol).</param>
/// <param name="SeedModels">What a fresh, offline sign-in can offer before the user edits the list.</param>
/// <param name="Note">Why this endpoint exists, for a reader of the table.</param>
public sealed record ProviderSubscriptionEndpoint(
    ProviderProtocol Protocol,
    string BaseUrl,
    IReadOnlyList<string> SeedModels,
    string? Note = null);

/// <summary>
/// One provider the app knows how to reach without asking the user for a URL: its display name, protocol, default base
/// URL, the environment variable the key normally lives in, and where to get a key. Nothing here is a secret and nothing
/// here is a model list the app depends on — model ids move, so the list is discovered from the provider when possible
/// (<see cref="SeedModels"/> is only what a fresh, offline install can offer before the first refresh).
/// </summary>
/// <param name="Subscription">
/// The subscription endpoint, when signing in reaches a different one than a key does; <c>null</c> for every preset whose
/// ways in arrive at the same address.
/// </param>
public sealed record ProviderPreset(
    string Id,
    string DisplayName,
    ProviderProtocol Protocol,
    string BaseUrl,
    ProviderAuthOptions Auth,
    string? EnvironmentVariable,
    string? Documentation,
    bool SupportsModelListing,
    IReadOnlyList<string> SeedModels,
    string? Note = null,
    ProviderSubscriptionEndpoint? Subscription = null);

/// <summary>The built-in provider catalogue. Order is the order the "add server" picker shows.</summary>
public static class ProviderPresets
{
    /// <summary>The preset id of the free-form entry, which has no base URL until the user types one.</summary>
    public const string CustomId = "custom-openai";

    /// <summary>Anthropic, by API key or by the local Claude Code subscription. The product's default provider.</summary>
    public const string AnthropicId = "anthropic";

    public const string OpenAiId = "openai";

    public const string CopilotId = "github-copilot";

    public const string OpenRouterId = "openrouter";

    public const string KimiId = "kimi-for-coding";

    public const string MetaId = "meta";

    public const string XAiId = "xai";

    public const string AnthropicBaseUrl = "https://api.anthropic.com";

    public const string AnthropicEnvironmentVariable = "ANTHROPIC_API_KEY";

    private static readonly IReadOnlyList<ProviderPreset> BuiltInPresets =
    [
        new(
            AnthropicId,
            "Anthropic",
            ProviderProtocol.Anthropic,
            AnthropicBaseUrl,
            ProviderAuthOptions.ApiKey | ProviderAuthOptions.ClaudeSubscription,
            AnthropicEnvironmentVariable,
            "https://platform.claude.com/settings/keys",
            SupportsModelListing: false,
            ["claude-opus-4-5", "claude-sonnet-4-5", "claude-haiku-4-5"]),
        new(
            OpenAiId,
            "OpenAI",
            ProviderProtocol.OpenAiCompatible,
            "https://api.openai.com/v1",
            // One vendor, two ways in: a platform key on api.openai.com, or the ChatGPT plan the user is signed in to. The
            // credential decides which endpoint is used, so there is one row in the picker and not two.
            ProviderAuthOptions.ApiKey | ProviderAuthOptions.ChatGptSubscription,
            "OPENAI_API_KEY",
            "https://platform.openai.com/api-keys",
            SupportsModelListing: true,
            [],
            Subscription: new ProviderSubscriptionEndpoint(
                ProviderProtocol.OpenAiResponses,
                // The Responses adapter appends "/responses", and a plan is served at .../backend-api/codex/responses: the plain
                // .../backend-api root answers a different, key-only API.
                "https://chatgpt.com/backend-api/codex",
                // No listing endpoint exists for a subscription, so a fresh sign-in starts from these seeds; they are the ids the
                // reference client serves, newest first, and a turn re-reads nothing until the user edits the list.
                ["gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol", "gpt-5.6-luna", "gpt-5.6-terra"],
                Note: "The model list comes from the subscription, the endpoint is /backend-api/codex/responses, and every request must carry the account header it draws on.")),
        new(
            CopilotId,
            "GitHub Copilot",
            ProviderProtocol.OpenAiCompatible,
            // The key way in uses this static host; the device-code sign-in replaces it with the API base the session names.
            CopilotAuthentication.DefaultApiHost,
            ProviderAuthOptions.ApiKey | ProviderAuthOptions.GitHubCopilot,
            "COPILOT_GITHUB_TOKEN",
            "https://github.com/features/copilot",
            SupportsModelListing: true,
            [],
            Note: "copilot.unofficial"),
        new(
            OpenRouterId,
            "OpenRouter",
            ProviderProtocol.OpenAiCompatible,
            "https://openrouter.ai/api/v1",
            // Both ways in produce an API key: a pasted one, or the one the browser sign-in returns. There is no token of
            // its own to refresh, so the credential kind of a signed-in OpenRouter provider is still ApiKey.
            ProviderAuthOptions.ApiKey | ProviderAuthOptions.OpenRouterOAuth,
            "OPENROUTER_API_KEY",
            "https://openrouter.ai/keys",
            SupportsModelListing: true,
            []),
        new(
            KimiId,
            "Kimi For Coding",
            ProviderProtocol.OpenAiCompatible,
            "https://api.kimi.com/coding/v1",
            ProviderAuthOptions.ApiKey | ProviderAuthOptions.KimiCode,
            "KIMI_API_KEY",
            "https://www.kimi.com/code",
            SupportsModelListing: false,
            ["k3"]),
        // Meta's models are Responses-only, so the protocol is the item-based one even though the host also answers a
        // Chat Completions path; a vendor that also speaks Chat Completions would stay on OpenAiCompatible.
        new(
            MetaId,
            "Meta",
            ProviderProtocol.OpenAiResponses,
            "https://api.meta.ai/v1",
            ProviderAuthOptions.ApiKey | ProviderAuthOptions.MetaSubscription,
            "META_API_KEY",
            "https://dev.meta.ai",
            SupportsModelListing: true,
            []),
        new(
            XAiId,
            "xAI",
            ProviderProtocol.OpenAiCompatible,
            "https://api.x.ai/v1",
            ProviderAuthOptions.ApiKey | ProviderAuthOptions.XAiSubscription,
            "XAI_API_KEY",
            "https://console.x.ai",
            SupportsModelListing: true,
            []),
    ];

    /// <summary>The free-form entry: any server that speaks the OpenAI protocol, at a URL the user supplies.</summary>
    public static ProviderPreset Custom { get; } = new(
        CustomId,
        "OpenAI-compatible",
        ProviderProtocol.OpenAiCompatible,
        string.Empty,
        ProviderAuthOptions.ApiKey,
        "OPENAI_API_KEY",
        null,
        SupportsModelListing: true,
        []);

    /// <summary>Every built-in provider, in presentation order; <see cref="Custom"/> is not part of it.</summary>
    public static IReadOnlyList<ProviderPreset> All => BuiltInPresets;

    /// <summary>The default provider a fresh install starts from: Anthropic.</summary>
    public static ProviderPreset Default => BuiltInPresets[0];

    /// <summary>Looks a preset up by id, including <see cref="Custom"/>; unknown ids return <c>null</c>.</summary>
    public static ProviderPreset? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (string.Equals(id, CustomId, StringComparison.OrdinalIgnoreCase))
        {
            return Custom;
        }

        foreach (var preset in BuiltInPresets)
        {
            if (string.Equals(preset.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        return null;
    }

    /// <summary>
    /// The preset a stored provider was created from, falling back to <see cref="Custom"/>: a provider whose preset id is
    /// gone still keeps its own protocol, URL and name, so it is shown as a custom entry rather than dropped.
    /// </summary>
    public static ProviderPreset FindOrDefault(string? id) => Find(id) ?? Custom;
}
