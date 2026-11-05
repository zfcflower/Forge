using System.Text;
using Forge.App.Terminal.Vt;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// Small helpers shared by the VT engine tests: building an emulator, feeding text as UTF-8,
/// and rendering rows/cells as strings so assertions read as plain text comparisons.
/// </summary>
internal static class VtHelpers
{
    public static TerminalEmulator Create(int columns, int rows, int scrollback, out VtTestHost host)
    {
        host = new VtTestHost();
        return new TerminalEmulator(columns, rows, scrollback, host);
    }

    public static void Feed(TerminalEmulator emulator, string text) =>
        emulator.Feed(Encoding.UTF8.GetBytes(text));

    public static void FeedBytes(TerminalEmulator emulator, params byte[] bytes) =>
        emulator.Feed(bytes);

    /// <summary>Renders one screen row (0-based from the top of the visible screen) with blanks as spaces.</summary>
    public static string RowText(TerminalEmulator emulator, int screenRow) =>
        LineText(emulator.GetLine(emulator.ScrollbackCount + screenRow));

    /// <summary>Renders one addressable line (scrollback or screen) with blanks as spaces, no trimming.</summary>
    public static string LineText(TerminalLine line)
    {
        var builder = new StringBuilder(line.Count);
        foreach (TerminalCell cell in line.Cells)
        {
            if (cell.IsWideContinuation)
            {
                continue;
            }

            builder.Append(cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune));
        }

        return builder.ToString();
    }

    public static TerminalCell ScreenCell(TerminalEmulator emulator, int screenRow, int column) =>
        emulator.GetLine(emulator.ScrollbackCount + screenRow)[column];

    public static (int row, int col) Cursor(TerminalEmulator emulator) =>
        (emulator.Cursor.Row, emulator.Cursor.Column);
}
