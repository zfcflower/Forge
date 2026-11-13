using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// The wire details of the official Claude Code client, as captured from it (CLI 2.1.317, and the values its SDK transport
/// sends unchanged). Kept in one place because they only make sense together: the entrypoint appears both in the billing
/// header block and in the user agent, and a request claiming one without the other is a contradiction rather than a
/// configuration.
/// </summary>
/// <remarks>
/// Captured from a real request: <c>system[0]</c> is the billing block, <c>system[1]</c> the identity sentence, then the
/// caller's own system prompt; every header below rides on the same request. See the provider spec for where each value
/// comes from and what it costs to change.
/// </remarks>
public static class AnthropicWire
{
    /// <summary>
    /// The identity sentence the official client sends for the SDK, VS Code and third-party-desktop entrypoints — not the
    /// older "official CLI" sentence, which is only the interactive terminal's.
    /// </summary>
    public const string Identity = "You are a Claude agent, built on Anthropic's Claude Agent SDK.";

    /// <summary>
    /// The cache lifetime the official client asks for on its breakpoints: one hour, not the five-minute default. A shorter one
    /// expires between two turns of the same conversation and pays for the same prefix twice.
    /// </summary>
    public const string CacheTtl = "1h";

    /// <summary>The beta that admits <see cref="CacheTtl"/>; an API-key request has to announce it before asking for one hour.</summary>
    public const string ExtendedCacheTtlBeta = "extended-cache-ttl-2025-04-11";

    /// <summary>The <c>x-app</c> value every entrypoint sends: it names the binary, not the surface.</summary>
    public const string AppHeader = "cli";

    /// <summary>The Anthropic TypeScript SDK version the official client reports.</summary>
    public const string StainlessPackageVersion = "0.128.0";

    /// <summary>The runtime version it reports.</summary>
    public const string StainlessRuntimeVersion = "v26.12.1";

    /// <summary>Languages the official client confirms it speaks; announced so the request reads like the real thing.</summary>
    public static string Language => "js";

    /// <summary>Runtime of the official client, announced in the same transport headers.</summary>
    public static string Runtime => "node";

    /// <summary>
    /// The beta features the official client announces (18), in its order. Several describe behavior this app does not
    /// implement; they are announced because the identity claims to be that client, and a shorter list is a fingerprint.
    /// </summary>
    public static IReadOnlyList<string> Betas { get; } =
    [
        "claude-code-20250219",
        "oauth-2025-04-20",
        "interleaved-thinking-2025-05-14",
        "context-management-2025-06-27",
        "prompt-caching-scope-2026-01-05",
        "per-turn-control-2026-07-01",
        "mid-conversation-tool-changes-2026-07-01",
        "inline-tools-2026-09-15",
        "advisor-tool-2026-03-01",
        "thinking-token-count-2026-05-13",
        "advanced-tool-use-2025-11-20",
        "mid-conversation-system-clear-at-2026-08-21",
        "effort-2025-11-24",
        "thinking-binding-controls-2026-08-01",
        "extended-cache-ttl-2025-04-11",
        "cache-diagnosis-2026-04-07",
        "message-threads-2026-08-12",
    ];

    /// <summary>The block the official client puts first in <c>system</c>; the server strips it before the model sees it.</summary>
    public static string BillingHeader(ClaudeWire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        var version = string.IsNullOrEmpty(wire.Build) ? wire.Version : wire.Version + "." + wire.Build;
        return $"x-anthropic-billing-header: cc_version={version}; cc_entrypoint={wire.Entrypoint};";
    }

    /// <summary>The user agent that repeats the same entrypoint, in the official client's exact shape.</summary>
    public static string UserAgent(ClaudeWire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return $"claude-cli/{wire.Version} (external, {wire.Entrypoint})";
    }
}
