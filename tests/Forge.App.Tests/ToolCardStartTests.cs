using Avalonia.Threading;
using Forge.Abstractions;
using Forge.App.Providers;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A tool call's card is built from its <c>AgentToolStarted</c> — the event the agent loop emits <em>before</em> it invokes the
/// tool — so the transcript has the call on screen while it runs, not only once its result arrives. That is the fact a
/// built-in tool hides: read, grep and edit return in milliseconds from inside the process, so both events land in one coalesced
/// UI pass and the renderer only ever paints the finished card. The card being built at the start is what can still be kept;
/// this pins it, so moving the row's creation to the result cannot pass unnoticed.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ToolCardStartTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-tool-card-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient http = new();

    public void Dispose()
    {
        http.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle can outlive the test; the temp directory is the OS's problem then.
        }
    }

    [Fact]
    public Task The_card_is_built_when_the_call_starts_not_when_it_returns() => SharedUi.RunAsync(async () =>
    {
        var session = await SessionAsync();
        try
        {
            var call = new ToolCall("call-1", "read", """{"file_path":"src/Forge.App/App.axaml"}""");

            // What the dispatcher does between two frames: one coalesced pass. By this point the tool has not returned, and the
            // card already exists and says it is running.
            session.OnAgentEvent(new AgentToolStarted("root", call));
            Dispatcher.UIThread.RunJobs();
            var row = Assert.Single(ToolRows(session));
            Assert.True(row.IsRunning);
            Assert.False(row.IsSucceeded);

            session.OnAgentEvent(new AgentToolFinished("root", call, new ToolResult("file contents")));
            Dispatcher.UIThread.RunJobs();

            Assert.Same(row, Assert.Single(ToolRows(session)));
            Assert.False(row.IsRunning);
            Assert.True(row.IsSucceeded);
        }
        finally
        {
            session.Dispose();
        }
    });

    private async Task<SessionWorkspaceViewModel> SessionAsync()
    {
        Directory.CreateDirectory(directory);
        await using var store = new SqliteSessionStore(directory);
        var root = await store.CreateAsync(new SessionCreate(directory, "root"), CancellationToken.None);
        return new SessionWorkspaceViewModel(
            root,
            runtime: null,
            checkpoints: null,
            store: new SqliteSessionStore(directory),
            new ProviderServices(Path.Combine(directory, "settings.json"), directory, http));
    }

    /// <summary>Every tool row the transcript holds right now, in the order the rounds and their cards keep them.</summary>
    private static List<ToolRowViewModel> ToolRows(SessionWorkspaceViewModel session)
    {
        var rows = new List<ToolRowViewModel>();
        foreach (var round in session.Transcript.Rounds)
        {
            if (round.Card is not { } card)
            {
                continue;
            }

            foreach (var block in card.Blocks)
            {
                if (block is TurnToolGroupViewModel group)
                {
                    rows.AddRange(group.Tools);
                }
                else if (block is ToolRowViewModel single)
                {
                    rows.Add(single);
                }
            }
        }

        return rows;
    }
}
