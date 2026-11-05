namespace Forge.Abstractions;

/// <summary>Size of a terminal in character cells.</summary>
public readonly record struct TerminalSize(int Columns, int Rows);

/// <summary>
/// Request to start an interactive terminal. <see cref="WorkingDirectory"/> must be the session worktree (architecture:
/// terminals are bound to their session's working directory). Empty <see cref="Shell"/> means the platform default.
/// </summary>
public sealed record TerminalStartRequest(
    string SessionId,
    string WorkingDirectory,
    TerminalSize Size,
    string? Shell = null,
    IReadOnlyList<string>? Arguments = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>
/// A running pseudo-terminal. Output bytes are the raw VT stream (UTF-8) in order; the reader completes when the
/// process exits and the stream drains. Input and resize after exit are ignored. Disposing kills the whole process tree.
/// </summary>
public interface ITerminalSession : IAsyncDisposable
{
    string Id { get; }

    System.Threading.Channels.ChannelReader<ReadOnlyMemory<byte>> Output { get; }

    /// <summary>Completes with the exit code once the process has exited and its process tree is gone.</summary>
    Task<int> Exited { get; }

    ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct);

    ValueTask ResizeAsync(TerminalSize size, CancellationToken ct);

    /// <summary>
    /// True while the shell has a foreground child (an editor, an agent CLI, a build): closing the terminal would end
    /// work the user started. False for an idle shell, after exit, and when the host no longer knows the terminal.
    /// </summary>
    ValueTask<bool> HasForegroundProcessAsync(CancellationToken ct);
}

/// <summary>
/// Starts pseudo-terminals. The app uses the Extension Host implementation (PTY creation needs P/Invoke, which the
/// architecture confines to the Host); the host relays bytes over IPC.
/// </summary>
public interface ITerminalHost
{
    ValueTask<ITerminalSession> StartAsync(TerminalStartRequest request, CancellationToken ct);
}
