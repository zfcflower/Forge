namespace Forge.PluginWorker;

/// <summary>Entry point of the JIT-side worker executable.</summary>
public static class Program
{
    private const int FatalError = 1;
    private const int UsageError = 2;

    public static async Task<int> Main(string[] args)
    {
        WorkerProcessGroup.JoinOwnGroup();

        if (!WorkerOptions.TryParse(args, out var options, out var error))
        {
            await Console.Error.WriteLineAsync(error).ConfigureAwait(false);
            return UsageError;
        }

        try
        {
            return await new WorkerServer(options).RunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"forge-plugin-worker: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return FatalError;
        }
    }
}
