using Forge.Abstractions;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.Core.Tests.Agents;

public sealed class AskUserToolTests
{
    private sealed class FakeHandler : IUserQuestionHandler
    {
        public UserQuestionRequest? Request { get; private set; }

        public int Calls { get; private set; }

        public Func<UserQuestionRequest, CancellationToken, ValueTask<UserQuestionResponse>> Handler { get; set; } =
            (_, _) => ValueTask.FromResult(UserQuestionResponse.Declined);

        public ValueTask<UserQuestionResponse> AskAsync(UserQuestionRequest request, CancellationToken ct)
        {
            Calls++;
            Request = request;
            return Handler(request, ct);
        }
    }

    private const string OneQuestion =
        """{"questions":[{"header":"Storage","question":"Which store?","options":[{"label":"SQLite","description":"embedded"},{"label":"Files"}]}]}""";

    private static ToolContext Context() => new("s1", Path.GetTempPath()) { AgentId = "a1", AgentName = "scout" };

    private static ValueTask<ToolResult> Run(AskUserTool tool, string json, CancellationToken ct = default) =>
        tool.InvokeAsync(new ToolCall("c1", AskUserTool.Name, json), Context(), ct);

    [Fact]
    public void Definition_is_read_only_and_named_ask_user()
    {
        var tool = new AskUserTool(new FakeHandler());

        Assert.Equal("ask_user", tool.Definition.Name);
        Assert.Equal(ToolSideEffect.ReadOnly, tool.SideEffect);
    }

    [Fact]
    public async Task Generates_ids_and_passes_agent_identity_to_the_handler()
    {
        var handler = new FakeHandler();
        var json = """{"questions":[{"header":"A","question":"a?","options":[{"label":"x"},{"label":"y"}]},{"id":"mine","header":"B","question":"b?","options":[{"label":"x"},{"label":"y"}],"multiSelect":true,"allowFreeText":false}]}""";

        await Run(new AskUserTool(handler), json);

        var request = handler.Request!;
        Assert.Equal("s1", request.SessionId);
        Assert.Equal("a1", request.AgentId);
        Assert.Equal("scout", request.AgentName);
        Assert.Equal(["q1", "mine"], request.Questions.Select(q => q.Id));
        Assert.False(request.Questions[0].MultiSelect);
        Assert.True(request.Questions[0].AllowFreeText);
        Assert.True(request.Questions[1].MultiSelect);
        Assert.False(request.Questions[1].AllowFreeText);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"questions":[]}""")]
    [InlineData("""{"questions":[{"question":"q?","options":[{"label":"a"},{"label":"b"}]}]}""")]
    [InlineData("""{"questions":[{"header":"h","options":[{"label":"a"},{"label":"b"}]}]}""")]
    [InlineData("""{"questions":[{"header":"h","question":"q?"}]}""")]
    [InlineData("""{"questions":[{"header":"h","question":"q?","options":[{"label":"a"}]}]}""")]
    [InlineData("""{"questions":[{"header":"h","question":"q?","options":[{"label":"a"},{"label":"a"}]}]}""")]
    [InlineData("""{"questions":[{"header":"h","question":"q?","options":[{"label":"a"},{"description":"no label"}]}]}""")]
    [InlineData("""{"questions":[{"header":"thirteen char","question":"q?","options":[{"label":"a"},{"label":"b"}]}]}""")]
    [InlineData("""{"questions":[{"id":"x","header":"h","question":"q?","options":[{"label":"a"},{"label":"b"}]},{"id":"x","header":"h","question":"q?","options":[{"label":"a"},{"label":"b"}]}]}""")]
    public async Task Invalid_input_is_an_error_and_never_reaches_the_handler(string json)
    {
        var handler = new FakeHandler();

        var result = await Run(new AskUserTool(handler), json);

        Assert.True(result.IsError);
        Assert.NotEmpty(result.Content);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task More_than_four_questions_or_six_options_is_an_error()
    {
        var q = """{"header":"h","question":"q?","options":[{"label":"a"},{"label":"b"}]}""";
        var five = $$"""{"questions":[{{string.Join(",", Enumerable.Repeat(q, 5))}}]}""";
        var seven = """{"questions":[{"header":"h","question":"q?","options":[{"label":"1"},{"label":"2"},{"label":"3"},{"label":"4"},{"label":"5"},{"label":"6"},{"label":"7"}]}]}""";
        var tool = new AskUserTool(new FakeHandler());

        Assert.True((await Run(tool, five)).IsError);
        Assert.True((await Run(tool, seven)).IsError);
    }

    [Fact]
    public async Task Answered_lists_labels_and_free_text_per_question()
    {
        var handler = new FakeHandler
        {
            Handler = (_, _) => ValueTask.FromResult(new UserQuestionResponse(
                UserQuestionOutcome.Answered,
                [new UserQuestionAnswer("q1", ["SQLite", "Files"], "or a cloud DB")])),
        };

        var result = await Run(new AskUserTool(handler), OneQuestion);

        Assert.False(result.IsError);
        Assert.Equal("Storage: SQLite, Files; other: or a cloud DB", result.Content);
    }

    [Fact]
    public async Task Answered_with_only_free_text_reports_other()
    {
        var handler = new FakeHandler
        {
            Handler = (_, _) => ValueTask.FromResult(new UserQuestionResponse(
                UserQuestionOutcome.Answered, [new UserQuestionAnswer("q1", [], "LMDB")])),
        };

        var result = await Run(new AskUserTool(handler), OneQuestion);

        Assert.Equal("Storage: other: LMDB", result.Content);
    }

    [Fact]
    public async Task Declined_tells_the_model_to_proceed_on_its_own_judgment()
    {
        var result = await Run(new AskUserTool(new FakeHandler()), OneQuestion);

        Assert.False(result.IsError);
        Assert.Contains("dismissed", result.Content);
        Assert.Contains("best judgment", result.Content);
    }

    [Fact]
    public async Task Cancelled_outcome_reports_cancelled()
    {
        var handler = new FakeHandler { Handler = (_, _) => ValueTask.FromResult(UserQuestionResponse.Cancelled) };

        var result = await Run(new AskUserTool(handler), OneQuestion);

        Assert.Equal("cancelled", result.Content);
    }

    [Fact]
    public async Task Cancelling_a_pending_question_returns_cancelled_instead_of_hanging()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler
        {
            Handler = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return UserQuestionResponse.Declined;
            },
        };

        var pending = Run(new AskUserTool(handler), OneQuestion, cts.Token).AsTask();
        await cts.CancelAsync();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("cancelled", result.Content);
    }

    [Fact]
    public async Task An_already_cancelled_token_does_not_ask()
    {
        var handler = new FakeHandler();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Run(new AskUserTool(handler), OneQuestion, cts.Token);

        Assert.Equal("cancelled", result.Content);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_throwing_handler_becomes_an_error_result()
    {
        var handler = new FakeHandler { Handler = (_, _) => throw new InvalidOperationException("boom") };

        var result = await Run(new AskUserTool(handler), OneQuestion);

        Assert.True(result.IsError);
        Assert.Contains("boom", result.Content);
    }

    private static AgentHost BuildHost(IUserQuestionHandler? handler) =>
        new(new AgentHostOptions
        {
            Providers = _ => new FakeProvider((_, _) => Turn.Say("done")),
            Tools = [],
            Layers = new PromptLayers("test system", null, null),
            QuestionHandler = handler,
        });

    [Fact]
    public void The_tool_is_registered_only_when_a_handler_is_set()
    {
        Assert.False(BuildHost(null).Tools.TryGet(AskUserTool.Name, out _));
        Assert.True(BuildHost(new FakeHandler()).Tools.TryGet(AskUserTool.Name, out _));
    }

    [Fact]
    public void The_default_policy_does_not_ask_for_ask_user()
    {
        var policy = new DefaultApprovalPolicy();
        var request = new ApprovalRequest(AskUserTool.Name, OneQuestion, ToolSideEffect.ReadOnly, Path.GetTempPath());

        Assert.Equal(ApprovalMode.Allow, policy.Evaluate(request));
    }
}
