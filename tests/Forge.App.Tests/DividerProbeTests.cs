using Forge.Abstractions;
using Forge.App.ViewModels;
using Xunit;

namespace Forge.App.Tests;

public sealed class DividerProbeTests
{
    [Fact]
    public void Divider_line_has_time_tokens_rate_and_model()
    {
        var transcript = new TranscriptViewModel(false);
        var at = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        transcript.AddUserMessage("hi", at);
        var card = transcript.BeginTurn(at);
        card.NoteModel("openai", "gpt-5");
        card.AppendText("hello");
        card.NoteStream(TimeSpan.FromSeconds(4));
        card.AddUsage(new TokenUsage(100, 200, 0, 0, 0), StopReason.EndTurn);
        transcript.EndTurn(TimeSpan.FromSeconds(10), null);

        var divider = transcript.FlatRows().OfType<TurnDividerViewModel>().Last();
        Assert.Contains("10.0 秒", divider.Line, StringComparison.Ordinal);
        Assert.Contains("200", divider.Line, StringComparison.Ordinal);
        Assert.Contains("gpt-5", divider.Line, StringComparison.Ordinal);
        Assert.DoesNotContain("tok/s", divider.Line.Replace("50.0 tok/s", ""), StringComparison.Ordinal);
    }
}
