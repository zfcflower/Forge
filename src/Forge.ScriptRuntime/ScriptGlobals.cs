namespace Forge.Scripting;

/// <summary>
/// The globals one script run sees: <c>Tools</c> for inner calls (the only way out of the process), <c>Log</c> for
/// captured output and <c>Cancellation</c> for long loops. The entry point creates exactly one instance per invocation,
/// so a run never reuses another run's facade, log or token.
/// </summary>
public class ScriptGlobals
{
    private readonly Action<object?> log;

    /// <summary>Public, and not sealed, so the compiled script type (a separate assembly) can derive from it and see
    /// <c>Tools</c>, <c>Log</c> and <c>Cancellation</c> as its own members, exactly as the old script engine exposed them.</summary>
    public ScriptGlobals(ToolsFacade tools, Action<object?> log, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(log);
        Tools = tools;
        this.log = log;
        Cancellation = cancellation;
    }

    public ToolsFacade Tools { get; }

    public CancellationToken Cancellation { get; }

    public void Log(object? value) => log(value);
}

/// <summary>
/// Raised for a failed, refused or rejected inner tool call; scripts may catch it. The main program owns approval and
/// result truncation, so a refusal arrives as the tool result text with <c>IsError</c> set: this module never re-derives
/// an approval decision and <see cref="Denied"/> is only set for calls the script surface refuses outright (calling
/// <c>run_code</c> from inside a script).
/// </summary>
public sealed class ToolCallException(string toolName, string message, bool denied = false) : Exception(message)
{
    public string ToolName { get; } = toolName;

    public bool Denied { get; } = denied;
}
