using System.Text;
using Forge.App.Terminal.Vt;

namespace Forge.App.Tests.Terminal;

/// <summary>
/// A recording <see cref="ITerminalEmulatorHost"/>: answers are kept per call so tests can assert exact
/// response bytes, and side effects (bell, title, clipboard) are counted instead of performed.
/// Default colors are fixed RGB values so OSC 10/11 answers are deterministic.
/// </summary>
internal sealed class VtTestHost : ITerminalEmulatorHost
{
    public List<byte[]> Responses { get; } = new();

    public int BellCount { get; private set; }

    public List<string> Titles { get; } = new();

    public List<string> ClipboardWrites { get; } = new();

    public TerminalColor DefaultForeground { get; set; } = TerminalColor.Rgb(0x11, 0x22, 0x33);

    public TerminalColor DefaultBackground { get; set; } = TerminalColor.Rgb(0x44, 0x55, 0x66);

    public void Respond(ReadOnlySpan<byte> bytes) => Responses.Add(bytes.ToArray());

    public void TitleChanged(string title) => Titles.Add(title);

    public void Bell() => BellCount++;

    public void ClipboardWrite(string text) => ClipboardWrites.Add(text);

    public string AllResponsesText()
    {
        var builder = new StringBuilder();
        foreach (byte[] chunk in Responses)
        {
            builder.Append(Encoding.Latin1.GetString(chunk));
        }

        return builder.ToString();
    }
}
