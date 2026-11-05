namespace Forge.App.Terminal.Vt;

/// <summary>Terminal modes the renderer and the key/mouse encoders need to know about (DECSET/DECRST plus SM/RM).</summary>
[Flags]
internal enum TerminalModes
{
    None = 0,

    /// <summary>DECCKM (1): cursor keys send <c>SS3</c> instead of <c>CSI</c>.</summary>
    ApplicationCursorKeys = 1 << 0,

    /// <summary>DECKPAM (ESC =): numeric keypad sends application sequences.</summary>
    ApplicationKeypad = 1 << 1,

    /// <summary>2004: pasted text is wrapped in <c>CSI 200 ~</c> … <c>CSI 201 ~</c>.</summary>
    BracketedPaste = 1 << 2,

    /// <summary>1004: focus in/out is reported to the application.</summary>
    FocusEvents = 1 << 3,

    /// <summary>47 / 1047 / 1049: the alternate screen buffer is active.</summary>
    AlternateScreen = 1 << 4,

    /// <summary>2026: the application asks for whole frames; the renderer should hold drawing until it is cleared.</summary>
    SynchronizedOutput = 1 << 5,

    /// <summary>DECAWM (7): a character written at the last column wraps to the next line.</summary>
    AutoWrap = 1 << 6,

    /// <summary>DECOM (6): cursor addressing is relative to the scrolling region.</summary>
    Origin = 1 << 7,

    /// <summary>DECSCNM (5): foreground and background are swapped at render time.</summary>
    ReverseVideo = 1 << 8,
}

/// <summary>Mouse reporting level (DECSET 9 / 1000 / 1002 / 1003).</summary>
internal enum MouseTracking
{
    None = 0,

    /// <summary>9: press only.</summary>
    X10 = 1,

    /// <summary>1000: press and release, plus wheel.</summary>
    Normal = 2,

    /// <summary>1002: presses, releases and drags while a button is held.</summary>
    ButtonEvent = 3,

    /// <summary>1003: every pointer movement.</summary>
    AnyEvent = 4,
}
