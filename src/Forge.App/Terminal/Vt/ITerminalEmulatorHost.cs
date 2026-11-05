namespace Forge.App.Terminal.Vt;

/// <summary>
/// The side effects the emulator needs from its owner. The emulator itself is pure: every byte it wants to write back
/// to the pseudo-terminal (device attribute / cursor position / colour answers) and everything the user must see
/// (bell, title, clipboard) goes through here. Implementations may be called from the thread that calls
/// <see cref="TerminalEmulator.Feed"/>; the caller owns the lock.
/// </summary>
internal interface ITerminalEmulatorHost
{
    /// <summary>Writes an answer (DA, DSR, CPR, OSC 10/11 repl) back to the application as pseudo-terminal input.</summary>
    void Respond(ReadOnlySpan<byte> bytes);

    /// <summary>OSC 0/1/2: the window title the application asked for.</summary>
    void TitleChanged(string title);

    /// <summary>BEL.</summary>
    void Bell();

    /// <summary>OSC 52: the application put <paramref name="text"/> on the clipboard (already Base64-decoded).</summary>
    void ClipboardWrite(string text);

    /// <summary>Default foreground, used to answer OSC 10 queries.</summary>
    TerminalColor DefaultForeground { get; }

    /// <summary>Default background, used to answer OSC 11 queries.</summary>
    TerminalColor DefaultBackground { get; }
}
