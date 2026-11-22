using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Forge.Core.Providers;
using Forge.IO.Tools;

namespace Forge.App.Providers;

/// <summary>
/// Lists the connected providers' models and the reasoning steps each one accepts, which is what <c>spawn_agent</c>'s
/// <c>model</c> and <c>thinking</c> arguments need. Read-only and free of approval. Its description is static bytes (the
/// model list is never inlined into it, so the cached prefix cannot move with a catalogue refresh) and the list is read
/// from the live catalogue on every call, the same instance the settings page and the runtimes read.
/// </summary>
internal sealed class ModelsTool(Func<ProviderCatalog> catalog, Func<string, bool>? isModelHidden)
    : ToolBase(Name, Description, SchemaJson, ToolSideEffect.ReadOnly)
{
    public const string Name = "models";

    /// <summary>Line budget for the whole answer; a hidden gateway can serve more models than a tool result should carry.</summary>
    private const int MaxModelLines = 400;

    private const string SchemaJson =
        """{"type":"object","properties":{"provider":{"type":"string","description":"Provider id or display name to list one provider; a gateway can serve hundreds of models."}},"additionalProperties":false}""";

    private const string Description =
        "List every model the connected providers serve, with the reasoning steps each model accepts, for spawn_agent's "
        + "'model', 'provider' and 'thinking' arguments (pass the model id exactly as listed here). Models the user hid from "
        + "the pickers are not listed. Pass 'provider' (an id or display name) to list one provider: a gateway can serve "
        + "hundreds of models.";

    protected override ValueTask<ToolResult> InvokeCoreAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken ct)
    {
        if (catalog() is not { } live)
        {
            return Refused("no provider is connected: connect one in Settings > Providers.");
        }

        var providers = new List<ProviderModels>(live.Providers.Count);
        foreach (var provider in live.Providers)
        {
            providers.Add(new ProviderModels(provider.Id, provider.DisplayName, provider.Models));
        }

        if (providers.Count == 0)
        {
            return Refused("no provider is connected: connect one in Settings > Providers.");
        }

        var filter = ToolJson.OptionalString(arguments, "provider");
        var scope = ModelResolver.MatchProviders(providers, filter);
        if (scope.Count == 0)
        {
            return Refused(
                $"unknown provider '{filter!.Trim()}'; connected: "
                + string.Join(", ", providers.Select(static p => p.ProviderId + " (" + p.DisplayName + ")"))
                + ".");
        }

        var visible = new List<(ProviderModels Provider, List<string> Models)>(scope.Count);
        var total = 0;
        foreach (var provider in scope)
        {
            var models = new List<string>(provider.Models.Count);
            foreach (var model in provider.Models)
            {
                if (isModelHidden?.Invoke(model) != true)
                {
                    models.Add(model);
                }
            }

            total += models.Count;
            visible.Add((provider, models));
        }

        var builder = new StringBuilder(1024);
        builder.Append("models: ").Append(scope.Count).Append(scope.Count == 1 ? " provider" : " providers")
            .Append(", ").Append(total).Append(total == 1 ? " model" : " models").Append('\n');

        var lines = 0;
        var truncated = false;
        foreach (var (provider, models) in visible)
        {
            if (lines >= MaxModelLines)
            {
                truncated = true;
                break;
            }

            builder.Append('\n').Append(provider.ProviderId).Append(" (").Append(provider.DisplayName).Append(")\n");
            if (models.Count == 0)
            {
                builder.Append("  (no visible models)\n");
                continue;
            }

            var width = models.Max(static model => model.Length);
            foreach (var model in models)
            {
                if (lines >= MaxModelLines)
                {
                    truncated = true;
                    break;
                }

                builder.Append("  ").Append(model.PadRight(width)).Append("  thinking: ")
                    .Append(string.Join(", ", Steps(live, provider.ProviderId, model))).Append('\n');
                lines++;
            }
        }

        if (truncated)
        {
            var hidden = total - lines;
            builder.Append("  ... ").Append(hidden).Append(hidden == 1 ? " more model" : " more models")
                .Append(" not shown; call models with provider=<id> to narrow the list.\n");
        }

        return new(new ToolResult(builder.ToString().TrimEnd()));
    }

    private static IReadOnlyList<string> Steps(ProviderCatalog live, string providerId, string model)
    {
        var steps = live.Find(providerId)?.ThinkingStepsFor(model);
        return steps is { Count: > 0 } ? [.. steps.Select(ThinkingEfforts.Name)] : ["off"];
    }

    private static ValueTask<ToolResult> Refused(string message) => new(new ToolResult(message, IsError: true));
}
