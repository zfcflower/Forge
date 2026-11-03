using System.Globalization;
using Forge.Ipc;
using Forge.Scripting;

namespace Forge.PluginWorker;

/// <summary>Command line of the worker executable: <c>--pipe &lt;name&gt;</c> (required, the Extension
/// Host's worker pipe) and <c>--max-concurrency &lt;n&gt;</c> (bounded parallel tool execution).</summary>
public sealed record WorkerOptions
{
    public const int DefaultMaxConcurrency = 8;
    public const int MaxAllowedConcurrency = 64;

    public const string Usage =
        "usage: forge-plugin-worker --pipe <name> [--max-concurrency <n>]";

    /// <summary>Name of the named pipe the Extension Host listens on.</summary>
    public string PipeName { get; init; } = "";

    /// <summary>Upper bound on tool executions running at the same time inside this worker.</summary>
    public int MaxConcurrency { get; init; } = DefaultMaxConcurrency;

    /// <summary>Upper bound on waiting for the host to accept the connection.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound on one <c>host.tool_call</c> relay; infinite by default because the main
    /// program may be waiting for user approval.</summary>
    public TimeSpan ToolCallTimeout { get; init; } = Timeout.InfiniteTimeSpan;

    /// <summary>Upper bound on waiting for an unloaded plugin's load context to be collected.</summary>
    public TimeSpan UnloadTimeout { get; init; } = PluginLoadContext.DefaultUnloadTimeout;

    public static bool TryParse(IReadOnlyList<string> args, out WorkerOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = new WorkerOptions();
        error = "";
        var pipeName = "";
        var concurrency = DefaultMaxConcurrency;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (TryMatch(argument, "--pipe", out var inlinePipe))
            {
                var value = inlinePipe ?? NextValue(args, ref index);
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = "'--pipe' requires a pipe name.";
                    return false;
                }

                try
                {
                    pipeName = IpcPipe.ValidatePipeName(value);
                }
                catch (ArgumentException ex)
                {
                    error = ex.Message;
                    return false;
                }

                continue;
            }

            if (TryMatch(argument, "--max-concurrency", out var inlineConcurrency))
            {
                var value = inlineConcurrency ?? NextValue(args, ref index);
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out concurrency) ||
                    concurrency < 1 ||
                    concurrency > MaxAllowedConcurrency)
                {
                    error = $"'--max-concurrency' expects an integer between 1 and {MaxAllowedConcurrency}.";
                    return false;
                }

                continue;
            }

            error = $"Unrecognized argument '{argument}'. {Usage}";
            return false;
        }

        if (pipeName.Length == 0)
        {
            error = $"'--pipe <name>' is required. {Usage}";
            return false;
        }

        options = new WorkerOptions { PipeName = pipeName, MaxConcurrency = concurrency };
        return true;
    }

    private static bool TryMatch(string argument, string name, out string? inline)
    {
        if (string.Equals(argument, name, StringComparison.Ordinal))
        {
            inline = null;
            return true;
        }

        if (argument.StartsWith(name + "=", StringComparison.Ordinal))
        {
            inline = argument[(name.Length + 1)..];
            return true;
        }

        inline = null;
        return false;
    }

    private static string? NextValue(IReadOnlyList<string> args, ref int index) =>
        index + 1 < args.Count ? args[++index] : null;
}
