namespace Forge.Abstractions;

/// <summary>
/// The three prompt layers, in fixed order: built-in system prompt, global agent, project agent.
/// Null/empty layers are skipped. Per-request text never goes in a layer: it rides the request's own trailing message, so
/// the prefix a provider caches stays byte-identical.
/// </summary>
public sealed record PromptLayers(string System, string? GlobalAgent, string? ProjectAgent);

public sealed record PromptInput(PromptLayers Layers, string? AgentRole);

/// <summary><see cref="StablePrefix"/> is byte-identical for identical inputs and is the cacheable part.</summary>
public sealed record ComposedPrompt(string StablePrefix);

public interface IPromptComposer
{
    ComposedPrompt Compose(PromptInput input);
}
