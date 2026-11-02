namespace Forge.Abstractions;

/// <summary>
/// The three prompt layers, in fixed order: built-in system prompt, global agent, project agent.
/// Null/empty layers are skipped. Dynamic text never goes in a layer; it goes in <see cref="PromptInput.DynamicContext"/>.
/// </summary>
public sealed record PromptLayers(string System, string? GlobalAgent, string? ProjectAgent);

public sealed record PromptInput(PromptLayers Layers, string? AgentRole, string? DynamicContext);

/// <summary>StablePrefix is byte-identical for identical inputs and is the cacheable part; DynamicSuffix follows it.</summary>
public sealed record ComposedPrompt(string StablePrefix, string DynamicSuffix)
{
    public string Full => DynamicSuffix.Length == 0 ? StablePrefix : StablePrefix + "\n\n" + DynamicSuffix;
}

public interface IPromptComposer
{
    ComposedPrompt Compose(PromptInput input);
}
