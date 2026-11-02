namespace Forge.CodeMode;

/// <summary>
/// Globals a script sees: <c>Tools</c> for inner tool calls, <c>Log</c> for captured output and
/// <c>Cancellation</c> for long loops. Host code supplies the instance; scripts only read it.
/// </summary>
public sealed class ScriptGlobals
{
    private readonly ScriptLog log;

    internal ScriptGlobals(ToolsFacade tools, ScriptLog log, CancellationToken cancellation)
    {
        Tools = tools;
        this.log = log;
        Cancellation = cancellation;
    }

    public ToolsFacade Tools { get; }

    public CancellationToken Cancellation { get; }

    public void Log(object? value) => log.Write(value);
}
