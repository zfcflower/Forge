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
}

/// <summary>
/// One provider the app knows how to reach without asking the user for a URL: its display name, protocol, default base
/// URL, the environment variable the key normally lives in, and where to get a key. Nothing here is a secret and nothing
/// here is a model list the app depends on — model ids move, so the list is discovered from the provider when possible
/// (<see cref="SeedModels"/> is only what a fresh, offline install can offer before the first refresh).
/// </summary>
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
    string? Note = null);

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

    public const string OpenCodeZenId = "opencode-zen";

    public const string OpenCodeGoId = "opencode-go";

    public const string XAiId = "xai";

    public const string DeepSeekId = "deepseek";

    /// <summary>A ChatGPT plan instead of an OpenAI platform key; the two are different products with different endpoints.</summary>
    public const string ChatGptId = "chatgpt";

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
            ProviderAuthOptions.ApiKey,
            "OPENAI_API_KEY",
            "https://platform.openai.com/api-keys",
            SupportsModelListing: true,
            []),
        new(
            CopilotId,
            "GitHub Copilot",
            ProviderProtocol.OpenAiCompatible,
            CopilotAuthentication.DefaultApiHost,
            ProviderAuthOptions.GitHubCopilot,
            null,
            "https://github.com/features/copilot",
            SupportsModelListing: true,
            [],
            Note: "copilot.unofficial"),
        new(
            OpenRouterId,
            "OpenRouter",
            ProviderProtocol.OpenAiCompatible,
            "https://openrouter.ai/api/v1",
            ProviderAuthOptions.ApiKey,
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
            ChatGptId,
            "OpenAI (ChatGPT)",
            ProviderProtocol.OpenAiResponses,
            // The Responses adapter appends "/responses", and a plan is served at .../backend-api/codex/responses: the plain
            // .../backend-api root answers a different, key-only API.
            "https://chatgpt.com/backend-api/codex",
            ProviderAuthOptions.ChatGptSubscription,
            null,
            "https://chatgpt.com",
            SupportsModelListing: false,
            // No listing endpoint exists for a subscription, so a fresh sign-in starts from these seeds; they are the ids the
            // reference client serves, newest first, and a turn re-reads nothing until the user edits the list.
            ["gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol", "gpt-5.6-luna", "gpt-5.6-terra"],
            Note: "The model list comes from the subscription, the endpoint is /backend-api/codex/responses, and every request must carry the account header it draws on."),
        new(
            OpenCodeZenId,
            "OpenCode Zen",
            ProviderProtocol.OpenAiCompatible,
            "https://opencode.ai/zen/v1",
            ProviderAuthOptions.ApiKey,
            "OPENCODE_API_KEY",
            "https://opencode.ai/docs/zen",
            SupportsModelListing: true,
            []),
        new(
            OpenCodeGoId,
            "OpenCode Go",
            ProviderProtocol.OpenAiCompatible,
            "https://opencode.ai/zen/go/v1",
            ProviderAuthOptions.ApiKey,
            "OPENCODE_API_KEY",
            "https://opencode.ai/docs/go",
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
        new(
            DeepSeekId,
            "DeepSeek",
            ProviderProtocol.OpenAiCompatible,
            "https://api.deepseek.com/v1",
            ProviderAuthOptions.ApiKey,
            "DEEPSEEK_API_KEY",
            "https://platform.deepseek.com/api_keys",
            SupportsModelListing: true,
            ["deepseek-chat", "deepseek-reasoner"]),
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
