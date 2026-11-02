using System.Security.Cryptography;
using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Prompts;

/// <summary>
/// Builds the three prompt layers plus the optional role text into one stable, cacheable prefix and
/// keeps all per-turn dynamic text after it.
/// </summary>
public sealed class PromptComposer : IPromptComposer
{
    public const string SystemHeading = "# System";
    public const string GlobalAgentHeading = "# Global Agent";
    public const string ProjectAgentHeading = "# Project Agent";
    public const string RoleHeading = "# Role";

    private const int PrefixKeyBytes = 16;

    public ComposedPrompt Compose(PromptInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Layers);
        var layers = input.Layers;

        var builder = new StringBuilder(2048);
        AppendSection(builder, SystemHeading, SystemLayer(layers.System));
        AppendLayer(builder, GlobalAgentHeading, layers.GlobalAgent);
        AppendLayer(builder, ProjectAgentHeading, layers.ProjectAgent);
        AppendLayer(builder, RoleHeading, input.AgentRole);

        var dynamicSuffix = string.IsNullOrWhiteSpace(input.DynamicContext) ? string.Empty : input.DynamicContext;
        return new ComposedPrompt(builder.ToString(), dynamicSuffix);
    }

    public ComposedPrompt Compose(PromptLayers layers, string? agentRole = null, string? dynamicContext = null) =>
        Compose(new PromptInput(layers, agentRole, dynamicContext));

    /// <summary>Cache key for a composed prompt; depends only on <see cref="ComposedPrompt.StablePrefix"/>.</summary>
    public static string PrefixKey(ComposedPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        Span<byte> hash = stackalloc byte[32];
        var length = Encoding.UTF8.GetByteCount(prompt.StablePrefix);
        if (length <= 1024)
        {
            Span<byte> utf8 = stackalloc byte[length];
            Encoding.UTF8.GetBytes(prompt.StablePrefix, utf8);
            SHA256.HashData(utf8, hash);
        }
        else
        {
            SHA256.HashData(Encoding.UTF8.GetBytes(prompt.StablePrefix), hash);
        }

        return Convert.ToHexStringLower(hash[..PrefixKeyBytes]);
    }

    private static string SystemLayer(string? systemText)
    {
        var builtIn = TextNormalizer.Normalize(BuiltInPrompts.System);
        var extra = TextNormalizer.Normalize(systemText);
        if (extra.Length == 0)
        {
            return builtIn;
        }

        return string.Concat(builtIn, "\n\n", extra);
    }

    private static void AppendLayer(StringBuilder builder, string heading, string? text)
    {
        var body = TextNormalizer.Normalize(text);
        if (body.Length == 0)
        {
            return;
        }

        AppendSection(builder, heading, body);
    }

    private static void AppendSection(StringBuilder builder, string heading, string body)
    {
        if (builder.Length > 0)
        {
            builder.Append("\n\n");
        }

        builder.Append(heading).Append("\n\n").Append(body);
    }
}
