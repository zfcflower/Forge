using Forge.Core.Tools;
using Xunit;

namespace Forge.Core.Tests.Tools;

/// <summary>Submit outcome wording: approved / rejected / invalid, message detail and empty-message fallbacks.</summary>
public sealed class ExtensionSubmitOutcomeTests
{
    private static ExtensionSubmitTool Tool(FakeExtensionAuthoring authoring, bool isUpdate = false) =>
        new(authoring, isUpdate);

    [Fact]
    public async Task Approved_names_the_tools_and_points_at_the_next_model_request()
    {
        var authoring = new FakeExtensionAuthoring
        {
            SubmitHandler = (_, _, _) => Task.FromResult(
                new ExtensionSubmitResult(ExtensionSubmitOutcome.Approved, "source hash 9f2c.", ["alpha", "beta"])),
        };
        var tool = Tool(authoring);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.PluginSubmit(id: "demo", version: "1.0.0")),
            ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.StartsWith("Approved: extension 'demo' version '1.0.0' is live", result.Content, StringComparison.Ordinal);
        Assert.Contains("'alpha' and 'beta'", result.Content, StringComparison.Ordinal);
        Assert.Contains("next model request", result.Content, StringComparison.Ordinal);
        Assert.Contains("source hash 9f2c.", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approved_without_tools_says_so()
    {
        var authoring = new FakeExtensionAuthoring
        {
            SubmitHandler = (_, _, _) => Task.FromResult(
                new ExtensionSubmitResult(ExtensionSubmitOutcome.Approved, string.Empty, [])),
        };
        var tool = Tool(authoring);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.PluginSubmit()), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.StartsWith("Approved: extension 'demo' version '1.0.0' is live", result.Content, StringComparison.Ordinal);
        Assert.Contains("registered no tool", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_is_not_an_error_and_reports_discard_with_the_reason()
    {
        var authoring = new FakeExtensionAuthoring
        {
            SubmitHandler = (_, _, _) => Task.FromResult(
                new ExtensionSubmitResult(ExtensionSubmitOutcome.Rejected, "The API key is missing.", ["alpha"])),
        };
        var tool = Tool(authoring);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.PluginSubmit()), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("rejected", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("discarded", result.Content, StringComparison.Ordinal);
        Assert.Contains("unchanged", result.Content, StringComparison.Ordinal);
        Assert.Contains("The API key is missing.", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejected_without_a_message_still_reports_the_base_text()
    {
        var authoring = new FakeExtensionAuthoring
        {
            SubmitHandler = (_, _, _) => Task.FromResult(
                new ExtensionSubmitResult(ExtensionSubmitOutcome.Rejected, string.Empty, [])),
        };
        var tool = Tool(authoring);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.PluginSubmit()), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("discarded", result.Content, StringComparison.Ordinal);
        Assert.Contains("unchanged", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Withdrawn_is_not_an_error_and_reports_the_draft_kept_pending()
    {
        var authoring = new FakeExtensionAuthoring
        {
            SubmitHandler = (_, _, _) => Task.FromResult(
                new ExtensionSubmitResult(
                    ExtensionSubmitOutcome.Withdrawn,
                    "the session closed or the app is shutting down.",
                    [])),
        };
        var tool = Tool(authoring);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.PluginSubmit(id: "demo", version: "1.0.0")),
            ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("withdrawn", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not a rejection", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kept pending", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not resend", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'demo'", result.Content, StringComparison.Ordinal);
        Assert.Contains("the session closed or the app is shutting down.", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Withdrawn_without_a_message_still_reports_the_base_text()
    {
        var authoring = new FakeExtensionAuthoring
        {
            SubmitHandler = (_, _, _) => Task.FromResult(
                new ExtensionSubmitResult(ExtensionSubmitOutcome.Withdrawn, string.Empty, [])),
        };
        var tool = Tool(authoring);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.PluginSubmit()), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("withdrawn", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kept pending", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not resend", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invalid_is_an_error_and_passes_the_diagnostics_through()
    {
        var authoring = new FakeExtensionAuthoring
        {
            SubmitHandler = (_, _, _) => Task.FromResult(
                new ExtensionSubmitResult(ExtensionSubmitOutcome.Invalid, "A.cs(3,7): error CS1002: ; expected.", [])),
        };
        var tool = Tool(authoring);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.PluginSubmit()), ExtensionArgs.Context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("A.cs(3,7): error CS1002: ; expected.", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_without_a_message_falls_back_to_a_default_reason()
    {
        var authoring = new FakeExtensionAuthoring
        {
            SubmitHandler = (_, _, _) => Task.FromResult(
                new ExtensionSubmitResult(ExtensionSubmitOutcome.Invalid, "   ", [])),
        };
        var tool = Tool(authoring);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.PluginSubmit(id: "demo", version: "1.0.0")),
            ExtensionArgs.Context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("was not accepted", result.Content, StringComparison.Ordinal);
        Assert.Contains("'demo'", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_outcome_is_an_error()
    {
        var authoring = new FakeExtensionAuthoring
        {
            SubmitHandler = (_, _, _) => Task.FromResult(
                new ExtensionSubmitResult((ExtensionSubmitOutcome)99, string.Empty, [])),
        };
        var tool = Tool(authoring);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.PluginSubmit()), ExtensionArgs.Context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("unknown outcome", result.Content, StringComparison.OrdinalIgnoreCase);
    }
}
