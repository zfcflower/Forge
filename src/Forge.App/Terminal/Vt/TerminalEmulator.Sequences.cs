namespace Forge.App.Terminal.Vt;

/// <summary>
/// The escape and control-sequence half of the emulator: every ESC final and every CSI final the engine understands.
/// Anything unrecognised falls through to a no-op so a broken or unsupported sequence can never corrupt the screen.
/// </summary>
internal sealed partial class TerminalEmulator
{
    private void DispatchCsiFinal(int final)
    {
        switch (final)
        {
            case '@':
                InsertCells(CountParam(0));
                return;
            case 'A':
                CursorUp(CountParam(0));
                return;
            case 'B':
                CursorDown(CountParam(0));
                return;
            case 'C':
                CursorForward(CountParam(0));
                return;
            case 'D':
                CursorBack(CountParam(0));
                return;
            case 'E':
                CursorDown(CountParam(0));
                CarriageReturn();
                return;
            case 'F':
                CursorUp(CountParam(0));
                CarriageReturn();
                return;
            case 'G':
                SetColumn(Param(0) - 1);
                return;
            case 'H':
            case 'f':
                CursorPosition(Param(0) - 1, Param(1) - 1);
                return;
            case 'I':
                HorizontalTab(CountParam(0));
                return;
            case 'J':
                EraseInDisplay(Param(0));
                return;
            case 'K':
                EraseInDisplayLine(Param(0));
                return;
            case 'L':
                InsertLines(CountParam(0));
                return;
            case 'M':
                DeleteLines(CountParam(0));
                return;
            case 'P':
                DeleteCells(CountParam(0));
                return;
            case 'S':
                ScrollUp(screen.ScrollTop, screen.ScrollBottom, Math.Min(CountParam(0), rows));
                return;
            case 'T':
                ScrollDown(screen.ScrollTop, screen.ScrollBottom, Math.Min(CountParam(0), rows));
                return;
            case 'X':
                EraseInLine(screen.CursorColumn, screen.CursorColumn + CountParam(0) - 1);
                return;
            case 'Z':
                BackTab(CountParam(0));
                return;
            case 'a':
                CursorForward(CountParam(0));
                return;
            case 'b':
                RepeatLastCharacter(CountParam(0));
                return;
            case 'c':
                ReportDeviceAttributes();
                return;
            case 'd':
                CursorRowAbsolute(Param(0) - 1);
                return;
            case 'e':
                CursorDown(CountParam(0));
                return;
            case 'g':
                ClearTabStop(Param(0));
                return;
            case 'h':
                SetModes(set: true);
                return;
            case 'l':
                SetModes(set: false);
                return;
            case 'm':
                if (privateMarker == 0)
                {
                    HandleSgr();
                }

                return;
            case 'n':
                ReportDeviceStatus();
                return;
            case 'p':
                if (intermediate == '!')
                {
                    SoftReset();
                }

                return;
            case 'q':
                if (intermediate == ' ')
                {
                    SetCursorStyle(Param(0));
                }

                return;
            case 'r':
                if (privateMarker == 0)
                {
                    SetScrollRegion(Param(0), Param(1));
                }

                return;
            case 's':
                SaveCursor();
                return;
            case 'u':
                RestoreCursor();
                return;
            case '`':
                SetColumn(Param(0) - 1);
                return;
            default:
                return;
        }
    }

    // ---- cursor movement ----------------------------------------------------------------------------------------

    private void CursorUp(int count)
    {
        TerminalScreen target = screen;
        int floor = target.CursorRow >= target.ScrollTop ? target.ScrollTop : 0;
        target.CursorRow = Math.Max(target.CursorRow - count, floor);
        target.PendingWrap = false;
    }

    private void CursorDown(int count)
    {
        TerminalScreen target = screen;
        int ceiling = target.CursorRow <= target.ScrollBottom ? target.ScrollBottom : rows - 1;
        target.CursorRow = Math.Min(target.CursorRow + count, ceiling);
        target.PendingWrap = false;
    }

    private void CursorForward(int count)
    {
        TerminalScreen target = screen;
        target.CursorColumn = Math.Min(target.CursorColumn + count, columns - 1);
        target.PendingWrap = false;
    }

    private void CursorBack(int count)
    {
        TerminalScreen target = screen;
        target.CursorColumn = Math.Max(target.CursorColumn - count, 0);
        target.PendingWrap = false;
    }

    private void SetColumn(int column) => MoveCursorTo(screen.CursorRow, column);

    private void CursorRowAbsolute(int row)
    {
        TerminalScreen target = screen;
        bool origin = (modes & TerminalModes.Origin) != 0;
        int originRow = origin ? target.ScrollTop : 0;
        int last = origin ? target.ScrollBottom : rows - 1;
        target.CursorRow = Math.Clamp(originRow + row, 0, last);
        target.CursorColumn = Math.Clamp(target.CursorColumn, 0, columns - 1);
        target.PendingWrap = false;
    }

    private void CursorPosition(int row, int column)
    {
        TerminalScreen target = screen;
        bool origin = (modes & TerminalModes.Origin) != 0;
        int originRow = origin ? target.ScrollTop : 0;
        int last = origin ? target.ScrollBottom : rows - 1;
        int targetRow = Math.Clamp(originRow + row, 0, last);
        int targetColumn = Math.Clamp(column, 0, columns - 1);
        MoveCursorTo(targetRow, targetColumn);
    }

    private void SetScrollRegion(int top, int bottom)
    {
        TerminalScreen target = screen;
        int topRow = Math.Clamp(top <= 0 ? 0 : top - 1, 0, rows - 1);
        int bottomRow = Math.Clamp(bottom <= 0 ? rows - 1 : bottom - 1, 0, rows - 1);
        if (bottomRow <= topRow)
        {
            return;
        }

        target.ScrollTop = topRow;
        target.ScrollBottom = bottomRow;
        MoveCursorTo((modes & TerminalModes.Origin) != 0 ? topRow : 0, 0);
    }

    private void Backspace()
    {
        TerminalScreen target = screen;
        if (target.CursorColumn > 0)
        {
            target.CursorColumn--;
        }

        target.PendingWrap = false;
    }

    private void CarriageReturn()
    {
        TerminalScreen target = screen;
        target.CursorColumn = 0;
        target.PendingWrap = false;
    }

    private void LineFeed()
    {
        Index();
        if (lineFeedNewLine)
        {
            screen.CursorColumn = 0;
        }
    }

    private void NextLine()
    {
        CarriageReturn();
        Index();
    }

    // ---- erasing & repeating ------------------------------------------------------------------------------------

    private void EraseInDisplay(int mode)
    {
        TerminalScreen target = screen;
        switch (mode)
        {
            case 0:
                EraseInLine(target.CursorColumn, columns - 1);
                for (int row = target.CursorRow + 1; row < rows; row++)
                {
                    EraseLineFully(row);
                }

                return;
            case 1:
                for (int row = 0; row < target.CursorRow; row++)
                {
                    EraseLineFully(row);
                }

                EraseInLine(0, target.CursorColumn);
                return;
            case 2:
                for (int row = 0; row < rows; row++)
                {
                    EraseLineFully(row);
                }

                return;
            case 3:
                ClearScrollback();
                return;
            default:
                return;
        }
    }

    private void EraseInDisplayLine(int mode)
    {
        TerminalScreen target = screen;
        switch (mode)
        {
            case 0:
                EraseInLine(target.CursorColumn, columns - 1);
                return;
            case 1:
                EraseInLine(0, target.CursorColumn);
                return;
            case 2:
                EraseLineFully(target.CursorRow);
                return;
            default:
                return;
        }
    }

    private int lastPrintedRune;

    private void RepeatLastCharacter(int count)
    {
        if (lastPrintedRune <= 0)
        {
            return;
        }

        count = Math.Min(count, columns * rows);
        for (int i = 0; i < count; i++)
        {
            PrintRune(lastPrintedRune);
        }
    }

    // ---- reporting ----------------------------------------------------------------------------------------------

    private void ReportDeviceStatus()
    {
        if (privateMarker == '?')
        {
            if (Param(0) == 6)
            {
                ReportCursorPosition("\u001b[?"u8);
            }

            return;
        }

        if (privateMarker != 0)
        {
            return;
        }

        switch (Param(0))
        {
            case 5:
                host.Respond("\u001b[0n"u8);
                return;
            case 6:
                ReportCursorPosition("\u001b["u8);
                return;
            default:
                return;
        }
    }

    private void ReportCursorPosition(ReadOnlySpan<byte> prefix)
    {
        Span<byte> buffer = stackalloc byte[64];
        AsciiWriter writer = new(buffer);
        writer.Add(prefix);
        writer.AddNumber(screen.CursorRow + 1);
        writer.Add((byte)';');
        writer.AddNumber(screen.CursorColumn + 1);
        writer.Add((byte)'R');
        host.Respond(writer.Written);
    }

    private void ReportDeviceAttributes()
    {
        if (privateMarker == '>')
        {
            host.Respond("\u001b[>0;10;1c"u8);
            return;
        }

        if (privateMarker == 0)
        {
            host.Respond("\u001b[?62;22c"u8);
        }
    }

    private void SetCursorStyle(int style)
    {
        switch (style)
        {
            case 0:
            case 1:
                cursorShape = CursorShape.Block;
                cursorBlinking = true;
                return;
            case 2:
                cursorShape = CursorShape.Block;
                cursorBlinking = false;
                return;
            case 3:
                cursorShape = CursorShape.Underline;
                cursorBlinking = true;
                return;
            case 4:
                cursorShape = CursorShape.Underline;
                cursorBlinking = false;
                return;
            case 5:
                cursorShape = CursorShape.Bar;
                cursorBlinking = true;
                return;
            case 6:
                cursorShape = CursorShape.Bar;
                cursorBlinking = false;
                return;
            default:
                return;
        }
    }

    // ---- modes --------------------------------------------------------------------------------------------------

    private void SetModes(bool set)
    {
        if (privateMarker == '?')
        {
            for (int i = 0; i < paramCount; i++)
            {
                SetDecMode(Param(i), set);
            }

            return;
        }

        if (privateMarker != 0)
        {
            return;
        }

        for (int i = 0; i < paramCount; i++)
        {
            switch (Param(i))
            {
                case 4:
                    insertMode = set;
                    break;
                case 20:
                    lineFeedNewLine = set;
                    break;
                default:
                    break;
            }
        }
    }

    private void SetDecMode(int mode, bool set)
    {
        switch (mode)
        {
            case 1:
                SetFlag(TerminalModes.ApplicationCursorKeys, set);
                return;
            case 5:
                SetFlag(TerminalModes.ReverseVideo, set);
                return;
            case 6:
                SetFlag(TerminalModes.Origin, set);
                MoveCursorTo(set ? screen.ScrollTop : 0, 0);
                return;
            case 7:
                SetFlag(TerminalModes.AutoWrap, set);
                return;
            case 12:
                cursorBlinking = set;
                return;
            case 25:
                cursorVisible = set;
                return;
            case 9:
                SetMouseMode(set ? MouseTracking.X10 : MouseTracking.None, MouseTracking.X10, set);
                return;
            case 47:
                SetAlternateScreen(set, clearOnEnter: false, clearOnExit: false);
                return;
            case 1000:
                SetMouseMode(set ? MouseTracking.Normal : MouseTracking.None, MouseTracking.Normal, set);
                return;
            case 1002:
                SetMouseMode(set ? MouseTracking.ButtonEvent : MouseTracking.None, MouseTracking.ButtonEvent, set);
                return;
            case 1003:
                SetMouseMode(set ? MouseTracking.AnyEvent : MouseTracking.None, MouseTracking.AnyEvent, set);
                return;
            case 1004:
                SetFlag(TerminalModes.FocusEvents, set);
                return;
            case 1006:
                mouseSgr = set;
                return;
            case 1047:
                SetAlternateScreen(set, clearOnEnter: false, clearOnExit: true);
                return;
            case 1048:
                if (set)
                {
                    SaveCursor();
                }
                else
                {
                    RestoreCursor();
                }

                return;
            case 1049:
                if (set)
                {
                    SaveCursor();
                    SetAlternateScreen(true, clearOnEnter: true, clearOnExit: false);
                }
                else
                {
                    SetAlternateScreen(false, clearOnEnter: false, clearOnExit: false);
                    RestoreCursor();
                }

                return;
            case 2004:
                SetFlag(TerminalModes.BracketedPaste, set);
                return;
            case 2026:
                SetFlag(TerminalModes.SynchronizedOutput, set);
                return;
            default:
                return;
        }
    }

    private void SetMouseMode(MouseTracking value, MouseTracking expected, bool set)
    {
        if (set)
        {
            mouseTracking = value;
        }
        else if (mouseTracking == expected)
        {
            mouseTracking = MouseTracking.None;
        }
    }

    private void SetFlag(TerminalModes flag, bool set)
    {
        if (set)
        {
            modes |= flag;
        }
        else
        {
            modes &= ~flag;
        }
    }

    // ---- SGR ----------------------------------------------------------------------------------------------------

    private void HandleSgr()
    {
        if (paramCount == 0)
        {
            ResetAttributes();
            return;
        }

        for (int i = 0; i < paramCount; i++)
        {
            int code = Param(i);
            switch (code)
            {
                case 0:
                    ResetAttributes();
                    break;
                case 1:
                    attributes |= CellFlags.Bold;
                    break;
                case 2:
                    attributes |= CellFlags.Dim;
                    break;
                case 3:
                    attributes |= CellFlags.Italic;
                    break;
                case 4:
                    ApplyUnderline(i);
                    break;
                case 5:
                case 6:
                    attributes |= CellFlags.Blink;
                    break;
                case 7:
                    attributes |= CellFlags.Inverse;
                    break;
                case 8:
                    attributes |= CellFlags.Hidden;
                    break;
                case 9:
                    attributes |= CellFlags.Strikethrough;
                    break;
                case 21:
                    attributes &= ~CellFlags.Underline;
                    attributes |= CellFlags.DoubleUnderline;
                    break;
                case 22:
                    attributes &= ~(CellFlags.Bold | CellFlags.Dim);
                    break;
                case 23:
                    attributes &= ~CellFlags.Italic;
                    break;
                case 24:
                    attributes &= ~(CellFlags.Underline | CellFlags.DoubleUnderline);
                    break;
                case 25:
                    attributes &= ~CellFlags.Blink;
                    break;
                case 27:
                    attributes &= ~CellFlags.Inverse;
                    break;
                case 28:
                    attributes &= ~CellFlags.Hidden;
                    break;
                case 29:
                    attributes &= ~CellFlags.Strikethrough;
                    break;
                case 30:
                case 31:
                case 32:
                case 33:
                case 34:
                case 35:
                case 36:
                case 37:
                    foreground = TerminalColor.Palette(code - 30);
                    break;
                case 38:
                    i += ApplyExtendedColor(i, foregroundColor: true);
                    break;
                case 39:
                    foreground = TerminalColor.Default;
                    break;
                case 40:
                case 41:
                case 42:
                case 43:
                case 44:
                case 45:
                case 46:
                case 47:
                    background = TerminalColor.Palette(code - 40);
                    break;
                case 48:
                    i += ApplyExtendedColor(i, foregroundColor: false);
                    break;
                case 49:
                    background = TerminalColor.Default;
                    break;
                case 90:
                case 91:
                case 92:
                case 93:
                case 94:
                case 95:
                case 96:
                case 97:
                    foreground = TerminalColor.Palette(code - 90 + 8);
                    break;
                case 100:
                case 101:
                case 102:
                case 103:
                case 104:
                case 105:
                case 106:
                case 107:
                    background = TerminalColor.Palette(code - 100 + 8);
                    break;
                default:
                    // 58/59 (underline colour) and everything unknown: ignored.
                    break;
            }
        }
    }

    private void ApplyUnderline(int group)
    {
        int style = subCounts[group] > 0 ? SubParam(group, 0) : 1;
        attributes &= ~(CellFlags.Underline | CellFlags.DoubleUnderline);
        switch (style)
        {
            case 0:
                return;
            case 2:
                attributes |= CellFlags.DoubleUnderline;
                return;
            default:
                attributes |= CellFlags.Underline;
                return;
        }
    }

    private void ResetAttributes()
    {
        attributes = CellFlags.None;
        foreground = TerminalColor.Default;
        background = TerminalColor.Default;
    }

    /// <summary>Reads <c>38</c>/<c>48</c> in both the semicolon and the colon form; returns extra parameters consumed.</summary>
    private int ApplyExtendedColor(int group, bool foregroundColor)
    {
        TerminalColor color = TerminalColor.Default;
        bool assigned = false;
        int consumed = 0;
        int extraCount = subCounts[group];
        if (extraCount > 0)
        {
            int mode = SubParam(group, 0);
            if (mode == 5 && extraCount >= 2)
            {
                color = TerminalColor.Palette(ClampByte(SubParam(group, 1)));
                assigned = true;
            }
            else if (mode == 2)
            {
                int offset = extraCount >= 5 ? 2 : 1;
                if (extraCount >= offset + 3)
                {
                    color = TerminalColor.Rgb(
                        ClampByte(SubParam(group, offset)),
                        ClampByte(SubParam(group, offset + 1)),
                        ClampByte(SubParam(group, offset + 2)));
                    assigned = true;
                }
            }
        }
        else if (group + 1 < paramCount)
        {
            int mode = Param(group + 1);
            if (mode == 5)
            {
                if (group + 2 < paramCount)
                {
                    color = TerminalColor.Palette(ClampByte(Param(group + 2)));
                    assigned = true;
                }

                consumed = 2;
            }
            else if (mode == 2)
            {
                if (group + 4 < paramCount)
                {
                    color = TerminalColor.Rgb(
                        ClampByte(Param(group + 2)),
                        ClampByte(Param(group + 3)),
                        ClampByte(Param(group + 4)));
                    assigned = true;
                }

                consumed = 4;
            }
        }

        if (assigned)
        {
            if (foregroundColor)
            {
                foreground = color;
            }
            else
            {
                background = color;
            }
        }

        return consumed;
    }

    private static byte ClampByte(int value) => (byte)Math.Clamp(value, 0, 255);
}
