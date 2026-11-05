using System.Globalization;
using Forge.Ipc;

namespace Forge.ScriptExecutor;

/// <summary>Command line of one single-use script executor: <c>--pipe &lt;name&gt;</c>, the Extension Host's executor
/// pipe. It accepts nothing else, so a stray argument can never widen what a spawned executor does.</summary>
internal sealed record ExecutorOptions
{
    public const string Usage = "usage: forge-script-executor --pipe <name>";

    public string PipeName { get; init; } = "";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public static bool TryParse(IReadOnlyList<string> args, out ExecutorOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);
        options = new ExecutorOptions();
        error = "";
        var pipeName = "";

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--pipe", StringComparison.Ordinal))
            {
                if (index + 1 >= args.Count)
                {
                    error = "'--pipe' requires a pipe name.";
                    return false;
                }

                pipeName = args[++index];
                continue;
            }

            if (argument.StartsWith("--pipe=", StringComparison.Ordinal))
            {
                pipeName = argument["--pipe=".Length..];
                continue;
            }

            error = string.Create(CultureInfo.InvariantCulture, $"Unrecognized argument '{argument}'. {Usage}");
            return false;
        }

        if (string.IsNullOrWhiteSpace(pipeName))
        {
            error = $"'--pipe <name>' is required. {Usage}";
            return false;
        }

        try
        {
            options = new ExecutorOptions { PipeName = IpcPipe.ValidatePipeName(pipeName) };
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }

        return true;
    }
}

/// <summary>
/// Entry point of the single-use JIT executor. It is spawned per <c>run_code</c> call, serves exactly one image and one
/// run, and is killed with its process tree by the host afterwards - it never starts a plugin, never compiles anything
/// and holds no cache, so nothing it did can be inherited by another call.
/// </summary>
public static class Program
{
    private const int FatalError = 1;
    private const int UsageError = 2;

    public static async Task<int> Main(string[] args)
    {
        if (!ExecutorOptions.TryParse(args, out var options, out var error))
        {
            await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
            return UsageError;
        }

        try
        {
            return await new ExecutorServer(options).RunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"forge-script-executor: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return FatalError;
        }
    }
}

/// <summary>The executor process: it connects to the host's executor pipe, serves <c>executor.load</c>,
/// <c>executor.run</c> and <c>executor.stop</c>, and exits when the host stops it or the pipe closes.</summary>
internal sealed class ExecutorServer(ExecutorOptions options)
{
    public ExecutorOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Connects, serves until <c>executor.stop</c> or disconnection, and reports the exit code: 0 for a clean
    /// stop, 1 when the connection failed.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Options.PipeName);

        var transportOptions = new IpcPipeOptions { ConnectTimeout = Options.ConnectTimeout };

        // No default deadline: the run carries its own budget, and the host keeps the hard deadline and the kill.
        await using var transport = new NamedPipeClientTransport(
            IpcPipe.ValidatePipeName(Options.PipeName),
            transportOptions);
        await using var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = Timeout.InfiniteTimeSpan });
        var session = new ExecutorSession(peer);
        await peer.StartAsync(ct).ConfigureAwait(false);
        return await session.ServeAsync(ct).ConfigureAwait(false);
    }
}
