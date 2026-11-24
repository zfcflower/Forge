using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The run-end event names the seq of the last message the turn persisted — the exact position a reopened transcript
/// pairs the turn's metrics row by. A wrong seq (the tool result's, the user message's) would pair the row with the
/// wrong card silently, which is worse than no row at all, so the value is read off the persist the loop itself just
/// awaited, never from a later query.
/// </summary>
public sealed class AgentTurnLastSeqTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-turn-last-seq-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then. Clearing the global
            // pool here would race every concurrently running store (the suite's flake window), for a temp dir.
        }
    }

    [Fact]
    public async Task The_completed_turn_names_the_seq_of_its_own_last_persisted_message()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var rootRow = await store.CreateSessionAsync(directory, "Root", CancellationToken.None);

        var provider = new ScriptedProvider();
        var host = new AgentHost(new AgentHostOptions
        {
            Providers = _ => provider,
            Tools = [],
            Layers = new PromptLayers("sys", null, null),
            Store = store,
        });
        var root = host.CreateRoot(rootRow.Id, rootRow.WorkspacePath);

        AgentTurnCompleted? completed = null;
        await foreach (var evt in host.RunAsync(root, "go", TestContext.Current.CancellationToken))
        {
            if (evt is AgentTurnCompleted done)
            {
                completed = done;
            }
        }

        Assert.NotNull(completed);

        // The turn persisted four messages: the user's text, the assistant that called the unregistered tool, the tool's
        // error result, and the assistant that ended the turn. The event names the last of those — not the tool result
        // before it, and not a later maximum that a next turn could have moved.
        var lastSeq = await store.LastSeqAsync(rootRow.Id, CancellationToken.None);
        Assert.Equal(lastSeq, completed!.LastSeq);
        Assert.True(lastSeq > 0);

        var messages = new List<StoredMessage>();
        await foreach (var stored in store.ReadAsync(rootRow.Id, 1, null, CancellationToken.None))
        {
            messages.Add(stored);
        }

        var last = messages[^1];
        Assert.Equal(last.Seq, completed.LastSeq);
        Assert.Equal(MessageRole.Assistant, last.Message.Role);
    }

    /// <summary>
    /// A provider whose calls answer from a script: the first call ends with a call to a tool no host registers, so the
    /// loop persists the error result and makes a second model call inside the same turn; the second call ends the turn.
    /// </summary>
    private sealed class ScriptedProvider : IChatProvider
    {
        private int calls;

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            var call = Interlocked.Increment(ref calls);
            if (call % 2 == 1)
            {
                yield return new TextDelta("partial");
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new ToolCallBlock($"c{call}", "no_such_tool", "{}")]),
                    TokenUsage.Empty,
                    StopReason.ToolUse);
            }
            else
            {
                yield return new MessageCompleted(
                    new ChatMessage(MessageRole.Assistant, [new TextBlock("done")]),
                    TokenUsage.Empty,
                    StopReason.EndTurn);
            }
        }
    }
}
