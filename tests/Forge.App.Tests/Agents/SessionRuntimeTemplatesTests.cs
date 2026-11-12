using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Forge.Abstractions;
using Forge.App.Agents;
using Forge.App.Prompts;
using Forge.Core.Prompts;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// A real <see cref="SessionRuntime"/> over a scripted model and temporary template directories: the system slot override
/// reaches the composed prefix once (append and replace), and a user agent file reaches the host's catalog with the
/// settings defaults applied while an explicit model in its file is kept.
/// </summary>
public sealed class SessionRuntimeTemplatesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-runtime-templates-" + Guid.NewGuid().ToString("N"));
    private readonly string user;
    private readonly string workspace;

    public SessionRuntimeTemplatesTests()
    {
        user = Path.Combine(root, "user");
        workspace = Path.Combine(root, "ws");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(workspace);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is harmless.
        }
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    private void Write(string relative, string content)
    {
        var path = Path.Combine(workspace, ".forge", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly SqliteSessionStore store;
        private readonly SqliteTodoStore todos;

        public Rig(string root, string workspace, string user, Func<string, ModelSettings?>? defaults)
        {
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            store = new SqliteSessionStore(data);
            todos = new SqliteTodoStore(data);
            var services = new AgentRuntimeServices(
                _ => Provider,
                HostClient: null,
                store,
                store,
                todos,
                new ModelSettings("main-model"))
            {
                PromptTemplates = new PromptTemplateService(user),
                AgentDefaults = defaults,
            };
            Runtime = new SessionRuntime(services, new SessionInfo("s1", workspace, "t", DateTimeOffset.UnixEpoch));
        }

        public ScriptedProvider Provider { get; } = new();

        public SessionRuntime Runtime { get; }

        public async Task TurnAsync() =>
            await Runtime.RunTurnAsync("hello", _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        public async ValueTask DisposeAsync()
        {
            Runtime.Dispose();
            await store.DisposeAsync();
            await todos.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_replace_override_of_the_system_slot_is_the_prefix_and_the_built_in_is_gone()
    {
        Write("prompts/system.md", "ZZ-REPLACED-SYSTEM-MARKER");
        await using var rig = new Rig(root, workspace, user, null);

        await rig.TurnAsync();

        var prompt = Assert.Single(rig.Provider.Requests).SystemPrompt;
        Assert.Equal(1, Count(prompt, "ZZ-REPLACED-SYSTEM-MARKER"));
        Assert.DoesNotContain(FirstLine(BuiltInPrompts.System), prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_append_override_keeps_the_built_in_once_and_adds_the_text_once()
    {
        Write("prompts/system.md", "---\nmode: append\n---\nZZ-APPENDED-SYSTEM-MARKER");
        await using var rig = new Rig(root, workspace, user, null);

        await rig.TurnAsync();

        var prompt = Assert.Single(rig.Provider.Requests).SystemPrompt;
        Assert.Equal(1, Count(prompt, "ZZ-APPENDED-SYSTEM-MARKER"));
        Assert.Equal(1, Count(prompt, FirstLine(BuiltInPrompts.System)));
        Assert.True(
            prompt.IndexOf(FirstLine(BuiltInPrompts.System), StringComparison.Ordinal) <
            prompt.IndexOf("ZZ-APPENDED-SYSTEM-MARKER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_an_override_the_built_in_system_prompt_is_used_once()
    {
        await using var rig = new Rig(root, workspace, user, null);

        await rig.TurnAsync();

        var prompt = Assert.Single(rig.Provider.Requests).SystemPrompt;
        Assert.Equal(1, Count(prompt, FirstLine(BuiltInPrompts.System)));
    }

    [Fact]
    public async Task A_user_agent_with_the_default_placeholder_gets_its_settings_entry_with_the_provider()
    {
        Write("agents/researcher.md", "---\ndescription: Digs into things\n---\nYou research.");
        await using var rig = new Rig(
            root,
            workspace,
            user,
            id => id == "researcher" ? new ModelSettings("tuned-model", ThinkingEffort.High, null, "p1") : null);

        var result = await rig.Runtime.SpawnChildAsync("researcher", "look", _ => { }, CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        var request = Assert.Single(rig.Provider.Requests);
        Assert.Equal("tuned-model", request.Model.Model);
        Assert.Equal(ThinkingEffort.High, request.Model.Thinking);
        Assert.Equal("p1", request.Model.ProviderId);
        Assert.Contains("You research.", request.SystemPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_agent_without_an_entry_follows_the_worker_fallback_the_defaults_function_returns()
    {
        Write("agents/researcher.md", "You research.");
        await using var rig = new Rig(root, workspace, user, _ => new ModelSettings("worker-model"));

        await rig.Runtime.SpawnChildAsync("researcher", "look", _ => { }, CancellationToken.None);

        Assert.Equal("worker-model", Assert.Single(rig.Provider.Requests).Model.Model);
    }

    [Fact]
    public async Task A_user_agent_with_an_explicit_model_keeps_it_over_the_settings_entry()
    {
        Write("agents/pinned.md", "---\nmodel: file-model\nthinking: low\n---\nYou are pinned.");
        await using var rig = new Rig(root, workspace, user, _ => new ModelSettings("settings-model", ThinkingEffort.High));

        await rig.Runtime.SpawnChildAsync("pinned", "look", _ => { }, CancellationToken.None);

        var request = Assert.Single(rig.Provider.Requests);
        Assert.Equal("file-model", request.Model.Model);
        Assert.Equal(ThinkingEffort.Low, request.Model.Thinking);
    }

    [Fact]
    public async Task A_built_in_kind_still_gets_its_settings_entry()
    {
        await using var rig = new Rig(
            root,
            workspace,
            user,
            id => id == AgentIds.Scout ? new ModelSettings("scout-model", ThinkingEffort.Medium, null, "p2") : null);

        await rig.Runtime.SpawnChildAsync("Scout", "look", _ => { }, CancellationToken.None);

        var request = Assert.Single(rig.Provider.Requests);
        Assert.Equal("scout-model", request.Model.Model);
        Assert.Equal("p2", request.Model.ProviderId);
    }

    [Fact]
    public async Task A_thinking_set_in_the_file_survives_the_settings_default_model()
    {
        Write("agents/researcher.md", "---\nthinking: high\n---\nYou research.");
        await using var rig = new Rig(
            root,
            workspace,
            user,
            id => id == "researcher" ? new ModelSettings("tuned-model", ThinkingEffort.Low, null, "p1") : null);

        var result = await rig.Runtime.SpawnChildAsync("researcher", "look", _ => { }, CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        var request = Assert.Single(rig.Provider.Requests);
        Assert.Equal("tuned-model", request.Model.Model);
        Assert.Equal("p1", request.Model.ProviderId);
        Assert.Equal(ThinkingEffort.High, request.Model.Thinking);
    }

    [Fact]
    public async Task A_user_agent_is_keyed_by_its_file_id_not_its_name()
    {
        // A name that gives no ASCII id still has a settings entry: the file name.
        Write("agents/researcher.md", "---\nname: 研究员\n---\nYou research.");
        await using var rig = new Rig(
            root,
            workspace,
            user,
            id => id == "researcher" ? new ModelSettings("tuned-model", ThinkingEffort.Medium) : null);

        var result = await rig.Runtime.SpawnChildAsync("研究员", "look", _ => { }, CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        Assert.Equal("tuned-model", Assert.Single(rig.Provider.Requests).Model.Model);
    }

    [Fact]
    public async Task A_project_file_named_like_scout_cannot_replace_scout_or_widen_its_tools()
    {
        string[] baseline;
        await using (var plain = new Rig(Path.Combine(root, "plain"), workspace, user, null))
        {
            await plain.Runtime.SpawnChildAsync("Scout", "look", _ => { }, CancellationToken.None);
            baseline = [.. Assert.Single(plain.Provider.Requests).Tools.Select(tool => tool.Name).Order(StringComparer.Ordinal)];
        }

        Write("agents/helper.md", "---\nname: Scout\n---\nI write files.");
        await using var rig = new Rig(root, workspace, user, null);

        var result = await rig.Runtime.SpawnChildAsync("Scout", "look", _ => { }, CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        var request = Assert.Single(rig.Provider.Requests);
        Assert.DoesNotContain("I write files.", request.SystemPrompt, StringComparison.Ordinal);
        Assert.Equal(baseline, request.Tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_edit_to_the_system_slot_during_a_session_does_not_change_its_prefix()
    {
        Write("prompts/system.md", "ZZ-FIRST-SYSTEM");
        await using var rig = new Rig(root, workspace, user, null);
        await rig.TurnAsync();

        Write("prompts/system.md", "ZZ-SECOND-SYSTEM");
        await rig.TurnAsync();

        var requests = rig.Provider.Requests;
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0].SystemPrompt, requests[1].SystemPrompt);
        Assert.Contains("ZZ-FIRST-SYSTEM", requests[1].SystemPrompt, StringComparison.Ordinal);
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private sealed class ScriptedProvider : IChatProvider
    {
        public ConcurrentQueue<ChatRequest> Queue { get; } = new();

        public IReadOnlyList<ChatRequest> Requests => [.. Queue];

        public string Id => "scripted";

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Queue.Enqueue(request);
            await Task.Yield();
            yield return new TextDelta("done");
            yield return new MessageCompleted(ChatMessage.Assistant("done"), new TokenUsage(3, 2), StopReason.EndTurn);
        }
    }
}
