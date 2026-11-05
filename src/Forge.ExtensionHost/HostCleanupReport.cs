namespace Forge.ExtensionHost;

/// <summary>
/// What the host could not clean up. Every path that stops a worker records what it could <em>not</em> verify - a native
/// containment kill that failed, a process still alive after the fallback kill, a worker stop that outlived the shutdown
/// window - so the end of the host never claims a clean exit it did not observe.
/// <para>
/// The record is written to stderr immediately (an operator sees the problem when it happens) and kept for the end of the
/// host: <c>RunAsync</c> returns a non-zero exit code and <c>DisposeAsync</c> throws instead of returning quietly when
/// anything is here. A best-effort Windows job-object kill is not treated as proof on its own: the verification is that
/// the process is gone, not that a native call was made.
/// </para>
/// </summary>
internal sealed class HostCleanupReport
{
    private readonly Lock sync = new();

    private readonly List<string> failures = [];

    public bool HasFailures
    {
        get
        {
            lock (sync)
            {
                return failures.Count > 0;
            }
        }
    }

    /// <summary>Records one cleanup problem. Never throws; a failure to report must not become the shutdown's failure.</summary>
    public void Record(string problem)
    {
        if (string.IsNullOrWhiteSpace(problem))
        {
            return;
        }

        lock (sync)
        {
            failures.Add(problem);
        }

        try
        {
            Console.Error.WriteLine("forge-extension-host: " + problem);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Records a cleanup exception as one readable line.</summary>
    public void Record(string what, Exception exception) => Record($"{what}: {exception.Message}");

    /// <summary>Logs a shutdown reason without marking the shutdown as failed; used when the host retires a connection on
    /// purpose and its own cleanup can still be verified.</summary>
    public void Diagnose(string message)
    {
        try
        {
            Console.Error.WriteLine("forge-extension-host: " + message);
        }
        catch (Exception)
        {
        }
    }

    public string[] Snapshot()
    {
        lock (sync)
        {
            return [.. failures];
        }
    }

    public string Describe() => string.Join("; ", Snapshot());
}
