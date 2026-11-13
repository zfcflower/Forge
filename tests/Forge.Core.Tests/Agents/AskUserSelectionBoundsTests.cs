using Forge.Abstractions;
using Forge.Core.Agents;
using Xunit;

namespace Forge.Core.Tests.Agents;

public sealed class AskUserSelectionBoundsTests
{
    private sealed class FakeHandler(UserQuestionResponse? response = null) : IUserQuestionHandler
    {
        public UserQuestionRequest? Request { get; private set; }

        public ValueTask<UserQuestionResponse> AskAsync(UserQuestionRequest request, CancellationToken ct)
        {
            Request = request;
            return ValueTask.FromResult(response ?? UserQuestionResponse.Declined);
        }
    }

    private static string Q(string extra, int options = 3) =>
        "{\"questions\":[{\"header\":\"h\",\"question\":\"q?\",\"options\":["
        + string.Join(",", Enumerable.Range(1, options).Select(i => $"{{\"label\":\"o{i}\"}}"))
        + "]" + (extra.Length > 0 ? "," + extra : "") + "}]}";

    private static ValueTask<ToolResult> Run(AskUserTool tool, string json) =>
        tool.InvokeAsync(new ToolCall("c1", AskUserTool.Name, json), new ToolContext("s1", Path.GetTempPath()), CancellationToken.None);

    [Fact]
    public async Task Valid_bounds_reach_the_handler()
    {
        var handler = new FakeHandler();

        var result = await Run(new AskUserTool(handler), Q("\"multiSelect\":true,\"minSelect\":2,\"maxSelect\":3"));

        Assert.False(result.IsError);
        Assert.Equal(2, handler.Request!.Questions[0].MinSelect);
        Assert.Equal(3, handler.Request.Questions[0].MaxSelect);
    }

    [Fact]
    public async Task Bounds_default_to_null_and_null_json_values_are_absent()
    {
        var handler = new FakeHandler();

        await Run(new AskUserTool(handler), Q("\"multiSelect\":true,\"minSelect\":null"));

        Assert.Null(handler.Request!.Questions[0].MinSelect);
        Assert.Null(handler.Request.Questions[0].MaxSelect);
    }

    [Fact]
    public async Task Free_text_counts_as_one_extra_pick_for_the_upper_bound()
    {
        var withText = new FakeHandler();
        var without = new FakeHandler();

        var ok = await Run(new AskUserTool(withText), Q("\"multiSelect\":true,\"maxSelect\":4"));
        var bad = await Run(new AskUserTool(without), Q("\"multiSelect\":true,\"allowFreeText\":false,\"maxSelect\":4"));

        Assert.False(ok.IsError);
        Assert.True(bad.IsError);
        Assert.Contains("maxSelect", bad.Content);
    }

    [Theory]
    [InlineData("\"multiSelect\":true,\"minSelect\":3,\"maxSelect\":2")]
    [InlineData("\"multiSelect\":true,\"maxSelect\":5")]
    [InlineData("\"multiSelect\":true,\"minSelect\":5")]
    [InlineData("\"multiSelect\":true,\"minSelect\":0")]
    [InlineData("\"multiSelect\":true,\"maxSelect\":-1")]
    [InlineData("\"multiSelect\":true,\"minSelect\":1.5")]
    [InlineData("\"multiSelect\":true,\"minSelect\":\"2\"")]
    [InlineData("\"multiSelect\":true,\"maxSelect\":true")]
    [InlineData("\"minSelect\":1")]
    [InlineData("\"maxSelect\":1")]
    [InlineData("\"multiSelect\":false,\"maxSelect\":2")]
    public async Task Invalid_bounds_are_an_error_and_never_reach_the_handler(string extra)
    {
        var handler = new FakeHandler();

        var result = await Run(new AskUserTool(handler), Q(extra));

        Assert.True(result.IsError);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Bounds_on_a_single_select_question_name_multiSelect_in_the_error()
    {
        var result = await Run(new AskUserTool(new FakeHandler()), Q("\"maxSelect\":1"));

        Assert.True(result.IsError);
        Assert.Contains("multiSelect", result.Content);
    }

    [Fact]
    public void Definition_mentions_bounds_and_skipping()
    {
        var tool = new AskUserTool(new FakeHandler());

        Assert.Contains("minSelect", tool.Definition.Description);
        Assert.Contains("skip", tool.Definition.Description);
        Assert.Contains("maxSelect", tool.Definition.InputSchemaJson);
    }

    [Fact]
    public async Task A_skipped_answer_is_reported_as_skipped_next_to_real_answers()
    {
        var json = "{\"questions\":[{\"header\":\"A\",\"question\":\"a?\",\"options\":[{\"label\":\"x\"},{\"label\":\"y\"}]},{\"header\":\"B\",\"question\":\"b?\",\"options\":[{\"label\":\"x\"},{\"label\":\"y\"}]}]}";
        var handler = new FakeHandler(new UserQuestionResponse(
            UserQuestionOutcome.Answered,
            [new UserQuestionAnswer("q1", ["x"]), new UserQuestionAnswer("q2", [], null, Skipped: true)]));

        var result = await Run(new AskUserTool(handler), json);

        Assert.False(result.IsError);
        Assert.Equal("A: x\nB: (skipped)", result.Content);
    }

    [Fact]
    public async Task All_skipped_answers_still_format()
    {
        var handler = new FakeHandler(new UserQuestionResponse(
            UserQuestionOutcome.Answered,
            [new UserQuestionAnswer("q1", [], null, Skipped: true)]));

        var result = await Run(new AskUserTool(handler), Q(""));

        Assert.False(result.IsError);
        Assert.Equal("h: (skipped)", result.Content);
    }
}