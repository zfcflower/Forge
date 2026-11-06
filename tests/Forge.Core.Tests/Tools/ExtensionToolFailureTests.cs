using Forge.Core.Tools;
using Xunit;

namespace Forge.Core.Tests.Tools;

/// <summary>Cancellation and failure mapping: OCE always propagates, other exceptions become error text.</summary>
public sealed class ExtensionToolFailureTests
{
    [Fact]
    public async Task List_cancellation_propagates_instead_of_becoming_a_result()
    {
        var authoring = new FakeExtensionAuthoring
        {
            ListHandler = _ => throw new OperationCanceledException(),
        };
        var tool = new ExtensionListTool(authoring);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tool.InvokeAsync(ExtensionArgs.Call(tool, "{}"), ExtensionArgs.Context, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submit_cancellation_propagates_instead_of_becoming_a_result(bool isUpdate)
    {
        var authoring = new FakeExtensionAuthoring
        {
            SubmitHandler = (_, _, _) => throw new OperationCanceledException(),
        };
        var tool = new ExtensionSubmitTool(authoring, isUpdate);
        var args = isUpdate ? ExtensionArgs.PluginSubmit() : ExtensionArgs.WorkflowSubmit();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tool.InvokeAsync(ExtensionArgs.Call(tool, args), ExtensionArgs.Context, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Disable_cancellation_propagates_instead_of_becoming_a_result()
    {
        var authoring = new FakeExtensionAuthoring
        {
            DisableHandler = (_, _, _) => throw new OperationCanceledException(),
        };
        var tool = new ExtensionDisableTool(authoring);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tool.InvokeAsync(ExtensionArgs.Call(tool, ExtensionArgs.IdAndVersion()), ExtensionArgs.Context, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Delete_cancellation_propagates_instead_of_becoming_a_result()
    {
        var authoring = new FakeExtensionAuthoring
        {
            DeleteHandler = (_, _, _) => throw new OperationCanceledException(),
        };
        var tool = new ExtensionDeleteTool(authoring);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => tool.InvokeAsync(ExtensionArgs.Call(tool, ExtensionArgs.IdAndVersion()), ExtensionArgs.Context, CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(false, "extension_create")]
    [InlineData(true, "extension_update")]
    public async Task Submit_failure_becomes_an_error_result(bool isUpdate, string expectedName)
    {
        var authoring = new FakeExtensionAuthoring
        {
            SubmitHandler = (_, _, _) => Task.FromException<ExtensionSubmitResult>(new InvalidOperationException("host is down")),
        };
        var tool = new ExtensionSubmitTool(authoring, isUpdate);
        var args = isUpdate ? ExtensionArgs.PluginSubmit() : ExtensionArgs.WorkflowSubmit();

        var result = await tool.InvokeAsync(ExtensionArgs.Call(tool, args), ExtensionArgs.Context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains($"{expectedName} failed: host is down", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disable_failure_becomes_an_error_result()
    {
        var authoring = new FakeExtensionAuthoring
        {
            DisableHandler = (_, _, _) => Task.FromException<string>(new InvalidOperationException("db is locked")),
        };
        var tool = new ExtensionDisableTool(authoring);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.IdAndVersion()), ExtensionArgs.Context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("extension_disable failed: db is locked", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_failure_becomes_an_error_result()
    {
        var authoring = new FakeExtensionAuthoring
        {
            DeleteHandler = (_, _, _) => Task.FromException<string>(new InvalidOperationException("db is locked")),
        };
        var tool = new ExtensionDeleteTool(authoring);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.IdAndVersion()), ExtensionArgs.Context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("extension_delete failed: db is locked", result.Content, StringComparison.Ordinal);
    }
}
