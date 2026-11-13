using MessagePack;

namespace Forge.Ipc.Protocol;

/// <summary>
/// The <c>terminal.*</c> operations between the main program and the host. A terminal is not a plugin API, so neither
/// the names nor the payloads are part of the SDK. Terminal bytes travel as MessagePack <c>bin</c>: one
/// <c>terminal.output</c> frame carries at most one PTY read (64 KiB), far below <see cref="IpcProtocol.MaxFrameBytes"/>.
/// </summary>
public static class TerminalOperations
{
    // Requests: main program → host.
    public const string Start = "terminal.start";       // TerminalStartRequest → TerminalStartResponse
    public const string Resize = "terminal.resize";     // TerminalResizeRequest → IpcEmpty
    public const string Foreground = "terminal.foreground"; // TerminalIdRequest → TerminalForegroundResponse
    public const string Stop = "terminal.stop";         // TerminalIdRequest → IpcEmpty

    // Notifications: main program → host.
    public const string Input = "terminal.input";       // TerminalData (Sequence unused)
    public const string Ack = "terminal.ack";           // TerminalAck

    // Notifications: host → main program.
    public const string Output = "terminal.output";     // TerminalData
    public const string Exit = "terminal.exit";         // TerminalExit; the last message a terminal id ever gets
}

/// <summary><c>terminal.start</c>. <see cref="WorkingDirectory"/> must be an existing absolute path; a null or empty
/// <see cref="Shell"/> means the platform default, and a null <see cref="Arguments"/> that shell's default arguments.</summary>
[MessagePackObject]
public sealed partial record TerminalStartRequest(
    [property: Key(0)] string SessionId,
    [property: Key(1)] string WorkingDirectory,
    [property: Key(2)] int Columns,
    [property: Key(3)] int Rows,
    [property: Key(4)] string? Shell = null,
    [property: Key(5)] IReadOnlyList<string>? Arguments = null,
    [property: Key(6)] IReadOnlyDictionary<string, string>? Environment = null);

[MessagePackObject]
public sealed partial record TerminalStartResponse(
    [property: Key(0)] string TerminalId,
    [property: Key(1)] int ProcessId,
    [property: Key(2)] string Shell);

/// <summary>Bytes of one terminal: PTY output (<c>terminal.output</c>, numbered by <see cref="Sequence"/>) or input for
/// the PTY (<c>terminal.input</c>, in send order).</summary>
[MessagePackObject]
public sealed partial record TerminalData(
    [property: Key(0)] string TerminalId,
    [property: Key(1)] long Sequence,
    [property: Key(2)] ReadOnlyMemory<byte> Data);

[MessagePackObject]
public sealed partial record TerminalResizeRequest(
    [property: Key(0)] string TerminalId,
    [property: Key(1)] int Columns,
    [property: Key(2)] int Rows);

/// <summary>Arguments of <c>terminal.foreground</c> and <c>terminal.stop</c>.</summary>
[MessagePackObject]
public sealed partial record TerminalIdRequest([property: Key(0)] string TerminalId);

[MessagePackObject]
public sealed partial record TerminalForegroundResponse([property: Key(0)] bool Busy);

/// <summary><c>terminal.ack</c>: how many output bytes the main program has consumed.</summary>
[MessagePackObject]
public sealed partial record TerminalAck(
    [property: Key(0)] string TerminalId,
    [property: Key(1)] long Bytes);

[MessagePackObject]
public sealed partial record TerminalExit(
    [property: Key(0)] string TerminalId,
    [property: Key(1)] int ExitCode);
