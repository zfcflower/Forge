using Forge.Plugin.Sdk;

namespace Forge.PluginWorker;

/// <summary>
/// The call context a worker accepts for one execution. The host assigns it and is its only authority: the worker checks
/// its shape and hands it to plugin code unchanged, so nothing a plugin does can widen it. The shape rule for the root is
/// the host's: the root is a call token, the top level's root is its own run id, and a nested context must already carry
/// the root it inherited - the worker never infers one from a plugin identity or from the first chain link. Refusing here
/// keeps plugin-visible contexts consistent even when the caller is an older host.
/// </summary>
internal static class WorkerCallContext
{
    private const int MaxRunIdLength = 128;

    /// <summary>
    /// Validates a host-bound context and returns it with <see cref="ExtensionCallContext.RootRunId"/> resolved. Returns
    /// <c>false</c> with a message when the context is missing, malformed, or is a nested call without a root.
    /// </summary>
    public static bool TryBind(ExtensionCallContext? context, out ExtensionCallContext bound, out string error)
    {
        bound = null!;
        error = string.Empty;
        if (context is null)
        {
            error = "'context' is required: the worker only runs an execution the host bound to a call context.";
            return false;
        }

        if (!IsRunToken(context.RunId))
        {
            error = "The call carries no usable run id; the execution is refused.";
            return false;
        }

        var chain = context.PluginChain;
        if (chain is null)
        {
            error = "The call carries no plugin chain; the execution is refused.";
            return false;
        }

        if (context.RootRunId is { } root)
        {
            if (!IsRunToken(root))
            {
                error = "The call carries a root run id outside [A-Za-z0-9_-] or longer than 128 chars; the execution is refused.";
                return false;
            }

            bound = context;
            return true;
        }

        if (chain.Count == 0)
        {
            bound = context with { RootRunId = context.RunId };
            return true;
        }

        error = "The call is nested but carries no root run id; the worker does not infer a root from plugin identities.";
        return false;
    }

    private static bool IsRunToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxRunIdLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
