using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>A turn carrying a PDF: it reaches the model, is stored whole, and the summarizer only ever sees a placeholder.</summary>
public sealed class FileCompactionTests
{
    private const string SummaryText = "SUMMARY";
    private static readonly string Base64 = Convert.ToBase64String(new byte[6000]);

    [Fact]
    public async Task A_file_turn_reaches_the_model_is_stored_and_is_summarised_as_a_placeholder()
    {
        using var directory = new TempDir();
        using var store = new SqliteSessionStore(directory.Path);
        var session = await store.CreateSessionAsync(directory.Path, "files", CancellationToken.None);
        var provider = new FakeProvider((request, _) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt ? Turn.Say(SummaryText) : Turn.Say(new string('a', 400), TokenUsage.Empty));
        var host = AgentTestHost.Create(
            provider,
            options: CompactionWindows.Legacy(200, maxTurns: 4),
            store: store);
        var root = host.CreateRoot(session.Id, directory.Path);

        var first = new ChatMessage(MessageRole.User, [new TextBlock("look at this"), new FileBlock("application/pdf", Base64, "report.pdf")]);
        Assert.Empty((await AgentTestHost.CollectAsync(host.RunAsync(root, first))).OfType<AgentFailed>());
        for (var turn = 2; turn <= 4; turn++)
        {
            Assert.Empty((await AgentTestHost.CollectAsync(host.RunAsync(root, $"turn {turn}"))).OfType<AgentFailed>());
        }

        var modelCall = provider.Requests.First(request => request.SystemPrompt != SummaryCompactor.SummarizerPrompt);
        Assert.Contains(modelCall.Messages[^1].Content, block => block is FileBlock { MediaType: "application/pdf" } file && file.Data == Base64);

        var stored = await AgentTestHost.CollectAsync(store.ReadAsync(session.Id, 1, null, CancellationToken.None));
        Assert.Equal(first.Content, stored[0].Message.Content);

        var summarizerCalls = provider.Requests.Where(request => request.SystemPrompt == SummaryCompactor.SummarizerPrompt).ToList();
        Assert.NotEmpty(summarizerCalls);
        Assert.All(summarizerCalls, call => Assert.All(call.Messages, message =>
        {
            Assert.DoesNotContain(message.Content, block => block is FileBlock);
            Assert.DoesNotContain(Base64, MessageJson.ExtractFullText(message), StringComparison.Ordinal);
        }));
        Assert.Contains(summarizerCalls, call => call.Messages.Any(message =>
            MessageJson.ExtractFullText(message).Contains("[file: report.pdf, 6 KB]", StringComparison.Ordinal)));
    }
}
