using System.Buffers;
using System.Text;

namespace Forge.App.Terminal.Vt;

/// <summary>
/// The DEC ANSI parser half of the emulator: UTF-8 decoding across <see cref="Feed"/> boundaries (a sequence split
/// between two reads resumes where it stopped, an invalid byte becomes U+FFFD), the DEC state machine
/// (ground / escape / csi_* / dcs_* / osc_string / sos_pm_apc_string) and the OSC handlers. Parameters are collected
/// into fixed arrays, capped at 32 parameters of 65535 and 8 colon sub-parameters each; OSC strings longer than 1 MiB
/// are dropped instead of buffered.
/// </summary>
internal sealed partial class TerminalEmulator
{
    private const int MaxParameters = 32;
    private const int MaxSubParameters = 8;
    private const int MaxParameterValue = 65535;
    private const int MaxOscLength = 1024 * 1024;
    private const int MaxHyperlinks = 4096;

    private const string DecGraphicsText =
        " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^" +
        " ◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    private readonly byte[] pending = new byte[4];
    private readonly int[] parameters = new int[MaxParameters];
    private readonly int[] subCounts = new int[MaxParameters];
    private readonly int[] subParameters = new int[MaxParameters * MaxSubParameters];
    private readonly Dictionary<string, int> hyperlinkIds = new(StringComparer.Ordinal);
    private readonly List<string> hyperlinks = [];
    private char[] oscBuffer = new char[256];
    private ParserState state;
    private int pendingCount;
    private int privateMarker;
    private int intermediate;
    private int intermediateCount;
    private int paramCount;
    private bool paramOverflow;
    private int groupPrimary;
    private int groupExtraCount;
    private bool groupCommitted;
    private int currentValue;
    private bool currentStarted;
    private int oscLength;
    private bool oscOverflow;

    private enum ParserState
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsIgnore,
        OscString,
        SosPmApcString,
    }

    private static ReadOnlySpan<char> DecGraphicsTable => DecGraphicsText;

    private void ResetParserState()
    {
        state = ParserState.Ground;
        pendingCount = 0;
        ResetSequence();
    }

    private void ResetSequence()
    {
        privateMarker = 0;
        intermediate = 0;
        intermediateCount = 0;
        paramCount = 0;
        paramOverflow = false;
        groupPrimary = 0;
        groupExtraCount = 0;
        groupCommitted = false;
        currentValue = 0;
        currentStarted = false;
    }

    private void Parse(ReadOnlySpan<byte> data)
    {
        int index = 0;
        while (index < data.Length)
        {
            if (state == ParserState.Ground)
            {
                int start = index;
                while (index < data.Length && (uint)(data[index] - 0x20) < 0x5Fu)
                {
                    index++;
                }

                if (index > start)
                {
                    PrintAscii(data[start..index]);
                    continue;
                }

                if (index >= data.Length)
                {
                    return;
                }

                byte control = data[index];
                if (control < 0x20)
                {
                    index++;
                    ExecuteControl(control);
                    continue;
                }

                if (control == 0x7F)
                {
                    index++;
                    continue;
                }
            }

            byte current = data[index];
            if (current < 0x80)
            {
                index++;
                ProcessChar(current);
                continue;
            }

            OperationStatus status = Rune.DecodeFromUtf8(data[index..], out Rune rune, out int consumed);
            if (status == OperationStatus.NeedMoreData)
            {
                int remaining = Math.Min(data.Length - index, pending.Length);
                data.Slice(index, remaining).CopyTo(pending);
                pendingCount = remaining;
                return;
            }

            if (status == OperationStatus.InvalidData)
            {
                index += consumed > 0 ? consumed : 1;
                ProcessRune(0xFFFD);
                continue;
            }

            index += consumed;
            ProcessRune(rune.Value);
        }
    }

    private void ProcessRune(int rune)
    {
        if ((uint)rune < 0xA0u)
        {
            if (state == ParserState.Ground)
            {
                ProcessC1(rune);
            }

            return;
        }

        switch (state)
        {
            case ParserState.Ground:
                PrintRune(rune);
                return;
            case ParserState.OscString:
                PushOscRune(rune);
                return;
            default:
                // Multi-byte text has no meaning inside an escape, CSI or DCS sequence.
                return;
        }
    }

    private void ProcessChar(int ch)
    {
        if (ch == 0x1B)
        {
            if (state == ParserState.OscString)
            {
                DispatchOsc();
            }

            EnterEscape();
            return;
        }

        if (ch == 0x18 || ch == 0x1A)
        {
            state = ParserState.Ground;
            ResetSequence();
            return;
        }

        switch (state)
        {
            case ParserState.Ground:
                if (ch < 0x20)
                {
                    ExecuteControl((byte)ch);
                }

                return;
            case ParserState.Escape:
                EscapeChar(ch);
                return;
            case ParserState.EscapeIntermediate:
                EscapeIntermediateChar(ch);
                return;
            case ParserState.CsiEntry:
                CsiEntryChar(ch);
                return;
            case ParserState.CsiParam:
                CsiParamChar(ch);
                return;
            case ParserState.CsiIntermediate:
                CsiIntermediateChar(ch);
                return;
            case ParserState.CsiIgnore:
                if (ch >= 0x40 && ch <= 0x7E)
                {
                    state = ParserState.Ground;
                }

                return;
            case ParserState.OscString:
                OscChar(ch);
                return;
            case ParserState.DcsEntry:
            case ParserState.DcsParam:
            case ParserState.DcsIntermediate:
            case ParserState.DcsPassthrough:
            case ParserState.DcsIgnore:
                DcsChar(ch);
                return;
            default:
                // sos_pm_apc_string: consume silently until ESC, CAN or SUB.
                return;
        }
    }

    private void ProcessC1(int rune)
    {
        switch (rune)
        {
            case 0x84:
                Index();
                return;
            case 0x85:
                NextLine();
                return;
            case 0x88:
                SetTabStop();
                return;
            case 0x8D:
                ReverseIndex();
                return;
            case 0x90:
                BeginString();
                state = ParserState.DcsEntry;
                return;
            case 0x98:
            case 0x9E:
            case 0x9F:
                BeginString();
                state = ParserState.SosPmApcString;
                return;
            case 0x9B:
                ResetSequence();
                state = ParserState.CsiEntry;
                return;
            case 0x9D:
                BeginString();
                state = ParserState.OscString;
                return;
            default:
                return;
        }
    }

    private void ExecuteControl(byte control)
    {
        switch (control)
        {
            case 0x07:
                host.Bell();
                return;
            case 0x08:
                Backspace();
                return;
            case 0x09:
                HorizontalTab(1);
                return;
            case 0x0A:
            case 0x0B:
            case 0x0C:
                LineFeed();
                return;
            case 0x0D:
                CarriageReturn();
                return;
            case 0x0E:
                activeCharset = 1;
                return;
            case 0x0F:
                activeCharset = 0;
                return;
            case 0x1B:
                EnterEscape();
                return;
            default:
                return;
        }
    }

    private void EnterEscape()
    {
        state = ParserState.Escape;
        ResetSequence();
    }

    private void EscapeChar(int ch)
    {
        switch (ch)
        {
            case '[':
                ResetSequence();
                state = ParserState.CsiEntry;
                return;
            case ']':
                BeginString();
                state = ParserState.OscString;
                return;
            case 'P':
                BeginString();
                state = ParserState.DcsEntry;
                return;
            case 'X':
            case '^':
            case '_':
                BeginString();
                state = ParserState.SosPmApcString;
                return;
            case '7':
                SaveCursor();
                state = ParserState.Ground;
                return;
            case '8':
                RestoreCursor();
                state = ParserState.Ground;
                return;
            case 'D':
                Index();
                state = ParserState.Ground;
                return;
            case 'E':
                NextLine();
                state = ParserState.Ground;
                return;
            case 'M':
                ReverseIndex();
                state = ParserState.Ground;
                return;
            case 'H':
                SetTabStop();
                state = ParserState.Ground;
                return;
            case 'c':
                FullReset();
                state = ParserState.Ground;
                return;
            case '=':
                modes |= TerminalModes.ApplicationKeypad;
                state = ParserState.Ground;
                return;
            case '>':
                modes &= ~TerminalModes.ApplicationKeypad;
                state = ParserState.Ground;
                return;
            default:
                if (ch >= 0x20 && ch <= 0x2F)
                {
                    intermediate = ch;
                    intermediateCount = 1;
                    state = ParserState.EscapeIntermediate;
                    return;
                }

                state = ParserState.Ground;
                return;
        }
    }

    private void EscapeIntermediateChar(int ch)
    {
        if (ch >= 0x20 && ch <= 0x2F)
        {
            intermediateCount++;
            return;
        }

        if (ch < 0x30 || ch > 0x7E)
        {
            return;
        }

        if (intermediateCount == 1)
        {
            switch (intermediate)
            {
                case '(':
                    g0Graphics = ch == '0';
                    break;
                case ')':
                    g1Graphics = ch == '0';
                    break;
                case '#':
                    if (ch == '8')
                    {
                        AlignmentTest();
                    }

                    break;
                default:
                    break;
            }
        }

        state = ParserState.Ground;
    }

    private void CsiEntryChar(int ch)
    {
        if (ch >= 0x30 && ch <= 0x39)
        {
            AccumulateDigit(ch - 0x30);
            state = ParserState.CsiParam;
            return;
        }

        if (ch == ';')
        {
            CommitValue();
            CloseGroup();
            state = ParserState.CsiParam;
            return;
        }

        if (ch == ':')
        {
            CommitValue();
            state = ParserState.CsiParam;
            return;
        }

        if (ch >= 0x3C && ch <= 0x3F)
        {
            privateMarker = ch;
            state = ParserState.CsiParam;
            return;
        }

        if (ch >= 0x20 && ch <= 0x2F)
        {
            intermediate = ch;
            intermediateCount = 1;
            state = ParserState.CsiIntermediate;
            return;
        }

        if (ch >= 0x40 && ch <= 0x7E)
        {
            DispatchCsi(ch);
        }
    }

    private void CsiParamChar(int ch)
    {
        if (ch >= 0x30 && ch <= 0x39)
        {
            AccumulateDigit(ch - 0x30);
            return;
        }

        if (ch == ';')
        {
            CommitValue();
            CloseGroup();
            return;
        }

        if (ch == ':')
        {
            CommitValue();
            return;
        }

        if (ch >= 0x3C && ch <= 0x3F)
        {
            state = ParserState.CsiIgnore;
            return;
        }

        if (ch >= 0x20 && ch <= 0x2F)
        {
            intermediate = ch;
            intermediateCount = 1;
            state = ParserState.CsiIntermediate;
            return;
        }

        if (ch >= 0x40 && ch <= 0x7E)
        {
            DispatchCsi(ch);
        }
    }

    private void CsiIntermediateChar(int ch)
    {
        if (ch >= 0x20 && ch <= 0x2F)
        {
            intermediateCount++;
            return;
        }

        if (ch >= 0x30 && ch <= 0x3F)
        {
            state = ParserState.CsiIgnore;
            return;
        }

        if (ch >= 0x40 && ch <= 0x7E)
        {
            DispatchCsi(ch);
        }
    }

    private void DispatchCsi(int final)
    {
        CommitValue();
        CloseGroup();
        state = ParserState.Ground;
        if (paramOverflow || intermediateCount > 1)
        {
            return;
        }

        DispatchCsiFinal(final);
    }

    private void DcsChar(int ch)
    {
        switch (state)
        {
            case ParserState.DcsEntry:
                if (ch >= 0x20 && ch <= 0x2F)
                {
                    state = ParserState.DcsIntermediate;
                }
                else if (ch >= 0x30 && ch <= 0x3F)
                {
                    state = ParserState.DcsParam;
                }
                else if (ch >= 0x40 && ch <= 0x7E)
                {
                    state = ParserState.DcsPassthrough;
                }

                return;
            case ParserState.DcsParam:
                if (ch >= 0x20 && ch <= 0x2F)
                {
                    state = ParserState.DcsIntermediate;
                }
                else if (ch >= 0x40 && ch <= 0x7E)
                {
                    state = ParserState.DcsPassthrough;
                }

                return;
            case ParserState.DcsIntermediate:
                if (ch >= 0x30 && ch <= 0x3F)
                {
                    state = ParserState.DcsIgnore;
                }
                else if (ch >= 0x40 && ch <= 0x7E)
                {
                    state = ParserState.DcsPassthrough;
                }

                return;
            default:
                // dcs_passthrough / dcs_ignore: the payload is parsed and thrown away.
                return;
        }
    }

    private void AccumulateDigit(int digit)
    {
        currentStarted = true;
        if (currentValue >= MaxParameterValue)
        {
            return;
        }

        currentValue = (currentValue * 10) + digit;
        if (currentValue > MaxParameterValue)
        {
            currentValue = MaxParameterValue;
        }
    }

    private void CommitValue()
    {
        int value = currentStarted ? currentValue : 0;
        currentValue = 0;
        currentStarted = false;
        if (groupCommitted)
        {
            if (groupExtraCount < MaxSubParameters && paramCount < MaxParameters && !paramOverflow)
            {
                subParameters[(paramCount * MaxSubParameters) + groupExtraCount] = value;
            }

            groupExtraCount++;
            return;
        }

        groupPrimary = value;
        groupCommitted = true;
    }

    private void CloseGroup()
    {
        if (paramCount < MaxParameters)
        {
            parameters[paramCount] = groupPrimary;
            subCounts[paramCount] = Math.Min(groupExtraCount, MaxSubParameters);
            paramCount++;
        }
        else
        {
            paramOverflow = true;
        }

        groupPrimary = 0;
        groupExtraCount = 0;
        groupCommitted = false;
    }

    private int Param(int index) => index < paramCount ? parameters[index] : 0;

    private int SubParam(int group, int index) =>
        index < subCounts[group] ? subParameters[(group * MaxSubParameters) + index] : 0;

    private int CountParam(int index)
    {
        int value = Param(index);
        return value <= 0 ? 1 : value;
    }

    // ---- OSC ----------------------------------------------------------------------------------------------------

    private void BeginString()
    {
        oscLength = 0;
        oscOverflow = false;
    }

    private void OscChar(int ch)
    {
        if (ch == 0x07)
        {
            DispatchOsc();
            state = ParserState.Ground;
            return;
        }

        if (ch < 0x20 || ch == 0x7F)
        {
            return;
        }

        PushOscChar((char)ch);
    }

    private void PushOscChar(char value)
    {
        if (oscOverflow)
        {
            return;
        }

        if (oscLength >= MaxOscLength)
        {
            oscOverflow = true;
            return;
        }

        if (oscLength == oscBuffer.Length)
        {
            Array.Resize(ref oscBuffer, Math.Min(oscBuffer.Length * 2, MaxOscLength));
        }

        oscBuffer[oscLength++] = value;
    }

    private void PushOscRune(int rune)
    {
        if (!Rune.TryCreate(rune, out Rune value))
        {
            return;
        }

        Span<char> chars = stackalloc char[2];
        int written = value.EncodeToUtf16(chars);
        for (int i = 0; i < written; i++)
        {
            PushOscChar(chars[i]);
        }
    }

    private void DispatchOsc()
    {
        if (!oscOverflow && oscLength > 0)
        {
            HandleOsc(oscBuffer.AsSpan(0, oscLength));
        }

        oscLength = 0;
        oscOverflow = false;
    }

    private void HandleOsc(ReadOnlySpan<char> data)
    {
        int separator = data.IndexOf(';');
        ReadOnlySpan<char> command = separator < 0 ? data : data[..separator];
        if (!TryParseNumber(command, out int code))
        {
            return;
        }

        ReadOnlySpan<char> payload = separator < 0 ? default : data[(separator + 1)..];
        switch (code)
        {
            case 0:
            case 1:
            case 2:
                SetTitle(payload);
                return;
            case 8:
                SetHyperlink(payload);
                return;
            case 10:
                AnswerColorQuery(payload, foregroundQuery: true);
                return;
            case 11:
                AnswerColorQuery(payload, foregroundQuery: false);
                return;
            case 52:
                WriteClipboard(payload);
                return;
            default:
                return;
        }
    }

    private static bool TryParseNumber(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        if (text.IsEmpty || text.Length > 5)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        return true;
    }

    private void SetTitle(ReadOnlySpan<char> payload)
    {
        if (payload.SequenceEqual(title))
        {
            return;
        }

        title = new string(payload);
        host.TitleChanged(title);
    }

    /// <summary>OSC 8: <c>8;params;uri</c>; an empty URI ends the link, ids are shared per URI.</summary>
    private void SetHyperlink(ReadOnlySpan<char> payload)
    {
        int separator = payload.IndexOf(';');
        if (separator < 0)
        {
            return;
        }

        ReadOnlySpan<char> uri = payload[(separator + 1)..];
        if (uri.IsEmpty)
        {
            hyperlink = 0;
            return;
        }

        Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> lookup =
            hyperlinkIds.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(uri, out int existing))
        {
            hyperlink = existing;
            return;
        }

        if (hyperlinks.Count >= MaxHyperlinks)
        {
            hyperlink = 0;
            return;
        }

        string key = new(uri);
        hyperlinks.Add(key);
        int id = hyperlinks.Count;
        hyperlinkIds[key] = id;
        hyperlink = id;
    }

    /// <summary>OSC 52: writes the decoded payload to the clipboard. Read requests (<c>?</c>) are ignored, never answered.</summary>
    private void WriteClipboard(ReadOnlySpan<char> payload)
    {
        int separator = payload.IndexOf(';');
        if (separator < 0)
        {
            return;
        }

        ReadOnlySpan<char> encoded = payload[(separator + 1)..];
        if (encoded.IsEmpty || (encoded.Length == 1 && encoded[0] == '?'))
        {
            return;
        }

        int maxBytes = ((encoded.Length * 3) / 4) + 3;
        Span<byte> buffer = maxBytes <= 512 ? stackalloc byte[512] : new byte[maxBytes];
        if (!Convert.TryFromBase64Chars(encoded, buffer, out int written))
        {
            return;
        }

        host.ClipboardWrite(Encoding.UTF8.GetString(buffer[..written]));
    }

    /// <summary>OSC 10/11 with a <c>?</c> argument: report the theme's default colours as <c>rgb:rrrr/gggg/bbbb</c>.</summary>
    private void AnswerColorQuery(ReadOnlySpan<char> payload, bool foregroundQuery)
    {
        if (payload.Length != 1 || payload[0] != '?')
        {
            return;
        }

        TerminalColor color = foregroundQuery ? host.DefaultForeground : host.DefaultBackground;
        byte red = color.IsRgb ? color.Red : foregroundQuery ? (byte)0xFF : (byte)0;
        byte green = color.IsRgb ? color.Green : foregroundQuery ? (byte)0xFF : (byte)0;
        byte blue = color.IsRgb ? color.Blue : foregroundQuery ? (byte)0xFF : (byte)0;
        Span<byte> buffer = stackalloc byte[48];
        AsciiWriter writer = new(buffer);
        writer.Add("\u001b]"u8);
        writer.AddNumber(foregroundQuery ? 10 : 11);
        writer.Add(";rgb:"u8);
        writer.AddHex16(red);
        writer.Add("/"u8);
        writer.AddHex16(green);
        writer.Add("/"u8);
        writer.AddHex16(blue);
        writer.Add("\u001b\\"u8);
        host.Respond(writer.Written);
    }
}
