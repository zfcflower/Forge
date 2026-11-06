using Forge.Abstractions;
using Forge.Core.Tools;
using Xunit;

namespace Forge.Core.Tests.Tools;

/// <summary><c>extension_list</c>: empty, populated, error-field and authoring-failure rendering.</summary>
public sealed class ExtensionListToolTests
{
    private static ExtensionListTool Tool(FakeExtensionAuthoring authoring) => new(authoring);

    [Fact]
    public async Task Empty_store_reports_no_extensions_and_no_pending_draft()
    {
        var authoring = new FakeExtensionAuthoring();
        var result = await Tool(authoring).InvokeAsync(ExtensionArgs.Call(Tool(authoring), "{}"), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("No extensions are installed and no draft is waiting for approval.", result.Content);
    }

    [Fact]
    public async Task Populated_store_reports_counts_tools_pending_and_errors()
    {
        var authoring = new FakeExtensionAuthoring
        {
            ListHandler = _ => Task.FromResult<IReadOnlyList<ExtensionSummary>>(
            [
                new("alpha", "1.0.0", "Active", ["alpha_tool", "alpha_helper"], HasPendingDraft: true),
                new("beta", "2.1.0", "Disabled", [], HasPendingDraft: false, Error: "last sync failed"),
            ]),
        };
        var tool = Tool(authoring);

        var result = await tool.InvokeAsync(ExtensionArgs.Call(tool, "{}"), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.StartsWith("2 extensions, 1 with a pending draft:\n", result.Content, StringComparison.Ordinal);
        Assert.Contains("- alpha 1.0.0 [Active] tools: alpha_tool, alpha_helper; pending draft", result.Content, StringComparison.Ordinal);
        Assert.Contains("- beta 2.1.0 [Disabled] tools: none; error: last sync failed", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_item_uses_singular_nouns()
    {
        var authoring = new FakeExtensionAuthoring
        {
            ListHandler = _ => Task.FromResult<IReadOnlyList<ExtensionSummary>>(
            [
                new("solo", "0.1.0", "Active", ["solo_tool"], HasPendingDraft: true),
            ]),
        };
        var tool = Tool(authoring);

        var result = await tool.InvokeAsync(ExtensionArgs.Call(tool, "{}"), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.StartsWith("1 extension, 1 with a pending draft:\n", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blank_error_is_not_rendered()
    {
        var authoring = new FakeExtensionAuthoring
        {
            ListHandler = _ => Task.FromResult<IReadOnlyList<ExtensionSummary>>(
            [
                new("plain", "1.0.0", "Active", [], HasPendingDraft: false, Error: "  "),
            ]),
        };
        var tool = Tool(authoring);

        var result = await tool.InvokeAsync(ExtensionArgs.Call(tool, "{}"), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.DoesNotContain("error:", result.Content, StringComparison.Ordinal);
        Assert.Contains("tools: none", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authoring_failure_becomes_an_error_result()
    {
        var authoring = new FakeExtensionAuthoring
        {
            ListHandler = _ => Task.FromException<IReadOnlyList<ExtensionSummary>>(new InvalidOperationException("store is down")),
        };
        var tool = Tool(authoring);

        var result = await tool.InvokeAsync(ExtensionArgs.Call(tool, "{}"), ExtensionArgs.Context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("extension_list failed: store is down", result.Content, StringComparison.Ordinal);
    }
}
