using Forge.Ipc;
using Forge.Ipc.Protocol;

namespace Forge.Integration.Tests.Terminal;

/// <summary>The <c>terminal.*</c> payloads the tests send and read. The shapes are the product's own
/// <see cref="Forge.Ipc.Protocol.TerminalMessages"/> records, so a test cannot drift from the wire the host serves.</summary>
internal static class TerminalWire
{
    public const string Start = TerminalOperations.Start;
    public const string Input = TerminalOperations.Input;
    public const string Resize = TerminalOperations.Resize;
    public const string Foreground = TerminalOperations.Foreground;
    public const string Stop = TerminalOperations.Stop;
    public const string Ack = TerminalOperations.Ack;
    public const string Output = TerminalOperations.Output;
    public const string Exit = TerminalOperations.Exit;

    public static TerminalStartRequest StartArgs(
        string workingDirectory,
        int columns,
        int rows,
        string? shell = null,
        string[]? arguments = null,
        Dictionary<string, string>? environment = null) =>
        new(
            "terminal-test-" + Guid.NewGuid().ToString("N")[..8],
            workingDirectory,
            columns,
            rows,
            shell,
            arguments,
            environment);

    public static TerminalData InputArgs(string terminalId, byte[] data) => new(terminalId, 0, data);

    public static TerminalResizeRequest ResizeArgs(string terminalId, int columns, int rows) =>
        new(terminalId, columns, rows);

    public static TerminalIdRequest IdArgs(string terminalId) => new(terminalId);

    public static TerminalAck AckArgs(string terminalId, long bytes) => new(terminalId, bytes);

    public static (string TerminalId, int ProcessId, string Shell) ReadStartResult(IpcMessage? response)
    {
        var started = Read<TerminalStartResponse>(response, "terminal.start");
        return (started.TerminalId, started.ProcessId, started.Shell ?? string.Empty);
    }

    public static bool ReadBusy(IpcMessage? response) =>
        Read<TerminalForegroundResponse>(response, "terminal.foreground").Busy;

    public static (string TerminalId, long Seq, byte[] Data) ReadOutput(IpcMessage? message)
    {
        var output = Read<TerminalData>(message, "terminal.output");
        return (output.TerminalId, output.Sequence, output.Data.ToArray());
    }

    public static (string TerminalId, int ExitCode) ReadExit(IpcMessage? message)
    {
        var exit = Read<TerminalExit>(message, "terminal.exit");
        return (exit.TerminalId, exit.ExitCode);
    }

    private static T Read<T>(IpcMessage? message, string what)
        where T : class =>
        message is null
            ? throw new InvalidOperationException($"{what} arrived without a payload.")
            : message.Read<T>();
}
