using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Agents;

/// <summary>
/// The script the attachment-compaction tests share, whichever kind of block the turn carries: the attachment reaches
/// the model untouched, is stored whole, and the summariser is asked to summarise while never seeing the block or its
/// bytes — only <c>[file: …]</c>-style placeholder text.
/// </summary>
internal static class AttachmentCompaction
{
    private const string SummaryText = "SUMMARY";

    /// <summary>Runs four turns whose first one carries <paramref name="attachment"/> in a workspace under
    /// <paramref name="workspace"/>, then asserts the model saw the block, the store kept it whole, and every
    /// summariser request carried <paramref name="placeholder"/> instead of <paramref name="payload"/>.</summary>
    public static async Task AssertSummarisedAsPlaceholderAsync(
        string workspace,
        string sessionTitle,
        ContentBlock attachment,
        string payload,
        string placeholder)
    {
        using var store = new SqliteSessionStore(workspace);
        var session = await store.CreateSessionAsync(workspace, sessionTitle, CancellationToken.None);
        var provider = new FakeProvider((request, _) =>
            request.SystemPrompt == SummaryCompactor.SummarizerPrompt ? Turn.Say(SummaryText) : Turn.Say(new string('a', 400), TokenUsage.Empty));
        var host = AgentTestHost.Create(
            provider,
            options: CompactionWindows.Legacy(200, maxTurns: 4),
            store: store);
        var root = host.CreateRoot(session.Id, workspace);

        var first = new ChatMessage(MessageRole.User, [new TextBlock("look at this"), attachment]);
        Assert.Empty((await AgentTestHost.CollectAsync(host.RunAsync(root, first))).OfType<AgentFailed>());
        for (var turn = 2; turn <= 4; turn++)
        {
            Assert.Empty((await AgentTestHost.CollectAsync(host.RunAsync(root, $"turn {turn}"))).OfType<AgentFailed>());
        }

        var modelCall = provider.Requests.First(request => request.SystemPrompt != SummaryCompactor.SummarizerPrompt);
        Assert.Contains(modelCall.Messages[^1].Content, block => block == attachment);

        var stored = await AgentTestHost.CollectAsync(store.ReadAsync(session.Id, 1, null, CancellationToken.None));
        Assert.Equal(first.Content, stored[0].Message.Content);

        var summarizerCalls = provider.Requests.Where(request => request.SystemPrompt == SummaryCompactor.SummarizerPrompt).ToList();
        Assert.NotEmpty(summarizerCalls);
        Assert.All(summarizerCalls, call => Assert.All(call.Messages, message =>
        {
            Assert.DoesNotContain(message.Content, block => block.GetType() == attachment.GetType());
            Assert.DoesNotContain(payload, MessageJson.ExtractFullText(message), StringComparison.Ordinal);
        }));
        Assert.Contains(summarizerCalls, call => call.Messages.Any(message =>
            MessageJson.ExtractFullText(message).Contains(placeholder, StringComparison.Ordinal)));
    }
}
